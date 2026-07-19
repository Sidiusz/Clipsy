using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <param name="InstallerUrl">browser_download_url of the .exe installer asset,
/// or null when the release has no installer attached.</param>
public sealed record UpdateInfo(string Version, string Url, string Notes, string? InstallerUrl, string? InstallerName);

/// <summary>Outcome of a release check, distinct from "an update exists".</summary>
public enum UpdateCheckStatus
{
    Found,       // a published release was located (see UpdateInfo)
    NoReleases,  // the repo simply has no releases — not an error
    Failed,      // network / API error — the check could not complete
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Info);

public static class UpdateService
{
    private const string ApiLatestUrl  = "https://api.github.com/repos/Sidiusz/Clipsy/releases/latest";
    // github.com/.../releases/latest 302s to the tagged release; on github.com
    // (not the API), so it dodges the unauthenticated API rate-limit/403s.
    private const string WebLatestUrl  = "https://github.com/Sidiusz/Clipsy/releases/latest";
    private const string ReleasesPage  = "https://github.com/Sidiusz/Clipsy/releases";
    private static readonly HttpClient _http;

    static UpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Clipsy/{CurrentVersion()} (+github.com/Sidiusz/Clipsy)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static async Task<UpdateCheckResult> CheckLatestAsync()
    {
        // Primary: GitHub API — richest data (assets, release notes).
        try
        {
            using var resp = await _http.GetAsync(ApiLatestUrl, HttpCompletionOption.ResponseContentRead);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new UpdateCheckResult(UpdateCheckStatus.NoReleases, null);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync();
                var info = ParseApiRelease(json);
                if (info != null) return new UpdateCheckResult(UpdateCheckStatus.Found, info);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[Clipsy] Update API returned {(int)resp.StatusCode}; trying web fallback.");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Update API check failed: {ex.Message}");
        }

        // Fallback: github.com web redirect. Survives API 403 / rate limits.
        return await CheckViaWebRedirectAsync();
    }

    private static UpdateInfo? ParseApiRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(tag)) return null;
        var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

        // Releases carry both a setup exe and a zip archive. Prefer the
        // asset with "setup" in its name; fall back to any .exe.
        string? installerUrl = null;
        string? installerName = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name == null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                var dlUrl = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                bool isSetup = name.Contains("setup", StringComparison.OrdinalIgnoreCase);
                if (installerUrl == null || isSetup)
                {
                    installerUrl = dlUrl;
                    installerName = name;
                    if (isSetup) break;
                }
            }
        }

        return new UpdateInfo(tag.TrimStart('v', 'V'), url, notes, installerUrl, installerName);
    }

    private static async Task<UpdateCheckResult> CheckViaWebRedirectAsync()
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Clipsy/{CurrentVersion()} (+github.com/Sidiusz/Clipsy)");

            using var resp = await http.GetAsync(WebLatestUrl, HttpCompletionOption.ResponseHeadersRead);
            var location = resp.Headers.Location?.ToString();
            if (string.IsNullOrEmpty(location))
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null);

            // .../releases/tag/<tag> → a release exists. .../releases → none.
            const string marker = "/releases/tag/";
            int idx = location.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return new UpdateCheckResult(UpdateCheckStatus.NoReleases, null);

            var tag = location[(idx + marker.Length)..].Trim('/');
            if (string.IsNullOrEmpty(tag))
                return new UpdateCheckResult(UpdateCheckStatus.NoReleases, null);

            // No assets/notes available this way — the notification falls back
            // to opening the release page for a manual download.
            var info = new UpdateInfo(tag.TrimStart('v', 'V'), location, "", null, null);
            return new UpdateCheckResult(UpdateCheckStatus.Found, info);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Update web fallback failed: {ex.Message}");
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null);
        }
    }

    public static bool IsNewer(string remote, string current)
    {
        if (!TryParseVersion(remote, out var r)) return false;
        if (!TryParseVersion(current, out var c)) return true;
        return r > c;
    }

    private static bool TryParseVersion(string s, out Version v)
    {
        v = new Version(0, 0, 0);
        if (string.IsNullOrEmpty(s)) return false;
        var clean = s.TrimStart('v', 'V').Split('-', '+')[0];
        var parts = clean.Split('.');
        try
        {
            int major = parts.Length > 0 ? int.Parse(parts[0]) : 0;
            int minor = parts.Length > 1 ? int.Parse(parts[1]) : 0;
            int build = parts.Length > 2 ? int.Parse(parts[2]) : 0;
            v = new Version(major, minor, build);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Downloads the installer to %TEMP%; returns its path or null on
    /// failure. Kept separate from launch so callers can pre-fetch silently.</summary>
    public static async Task<string?> DownloadInstallerAsync(UpdateInfo info, IProgress<double>? progress = null)
    {
        if (string.IsNullOrEmpty(info.InstallerUrl)) return null;
        try
        {
            var fileName = $"ClipsySetup-{info.Version}.exe";
            var path = Path.Combine(Path.GetTempPath(), fileName);

            using (var response = await _http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var src = await response.Content.ReadAsStreamAsync();
                await using var dst = File.Create(path);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    if (total is > 0) progress?.Report((double)read / total.Value);
                }
                progress?.Report(1.0);
            }

            var fi = new FileInfo(path);
            return (fi.Exists && fi.Length >= 1024) ? path : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Update download failed: {ex.Message}");
            Diagnostics.Log("UpdateService.DownloadInstaller", ex);
            return null;
        }
    }

    /// <summary>Launches a downloaded installer; the caller must exit so the exe
    /// can overwrite the running app.</summary>
    public static bool LaunchInstaller(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("UpdateService.LaunchInstaller", ex);
            return false;
        }
    }

    public static string CurrentVersion()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public static bool ShouldCheckNow(string interval, DateTime lastCheckUtc)
    {
        var span = interval switch
        {
            "hourly" => TimeSpan.FromHours(1),
            "daily" => TimeSpan.FromDays(1),
            "weekly" => TimeSpan.FromDays(7),
            "monthly" => TimeSpan.FromDays(30),
            "never" => TimeSpan.MaxValue,
            _ => TimeSpan.FromDays(1),
        };
        if (span == TimeSpan.MaxValue) return false;
        return DateTime.UtcNow - lastCheckUtc >= span;
    }
}
