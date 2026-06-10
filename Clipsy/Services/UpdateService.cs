using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <param name="InstallerUrl">browser_download_url of the .exe installer asset,
/// or null when the release has no installer attached.</param>
public sealed record UpdateInfo(string Version, string Url, string Notes, string? InstallerUrl, string? InstallerName);

public static class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/Sidiusz/Clipsy/releases/latest";
    private static readonly HttpClient _http;

    static UpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Clipsy/{CurrentVersion()} (+github.com/Sidiusz/Clipsy)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static async Task<UpdateInfo?> CheckLatestAsync()
    {
        try
        {
            var json = await _http.GetStringAsync(LatestReleaseUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(tag)) return null;
            var url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

            // Pick the first .exe asset as the installer (BuildInstaller.cmd
            // produces a single setup exe per release).
            string? installerUrl = null;
            string? installerName = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name == null || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    installerUrl = asset.TryGetProperty("browser_download_url", out var d) ? d.GetString() : null;
                    installerName = name;
                    break;
                }
            }

            return new UpdateInfo(tag.TrimStart('v', 'V'), url, notes, installerUrl, installerName);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Update check failed: {ex.Message}");
            return null;
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

    /// <summary>
    /// Downloads the release installer to %TEMP% and launches it. Returns the
    /// started installer process on success so the caller can exit the app
    /// (the running exe must be released before the installer overwrites it).
    /// </summary>
    public static async Task<bool> DownloadAndLaunchInstallerAsync(UpdateInfo info)
    {
        if (string.IsNullOrEmpty(info.InstallerUrl)) return false;
        try
        {
            var fileName = $"ClipsySetup-{info.Version}.exe";
            var path = Path.Combine(Path.GetTempPath(), fileName);

            using (var response = await _http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var src = await response.Content.ReadAsStreamAsync();
                await using var dst = File.Create(path);
                await src.CopyToAsync(dst);
            }

            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 1024) return false;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Update download failed: {ex.Message}");
            Diagnostics.Log("UpdateService.DownloadAndLaunchInstaller", ex);
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
