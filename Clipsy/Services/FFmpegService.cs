using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <summary>FFmpeg binary lifecycle (download/manage) and GIF conversion.
/// VP9/AV1 recording lives in FFmpegRecordingService.</summary>
public sealed class FFmpegService
{
    private static readonly Lazy<FFmpegService> _instance = new(() => new FFmpegService());
    public static FFmpegService Instance => _instance.Value;

    private readonly string _ffmpegDir;
    private readonly string _ffmpegExe;

    // BtbN GitHub build (x64, ~150 MB); gyan.dev kept only as fallback.
    private const string FFMPEG_URL =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private const string FFMPEG_URL_FALLBACK =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    private FFmpegService()
    {
        _ffmpegDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipsy", "ffmpeg");
        _ffmpegExe = Path.Combine(_ffmpegDir, "ffmpeg.exe");
    }

    // ─── Public surface ───────────────────────────────────────────────────────

    public string  ExePath     => _ffmpegExe;
    public bool    IsAvailable => File.Exists(_ffmpegExe);

    /// <summary>Download ffmpeg.exe. Reports (0-100, message). Cancellable.</summary>
    public async Task<bool> DownloadAsync(
        IProgress<(int Percent, string Message)> progress,
        CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(_ffmpegDir);
            var zipPath = Path.Combine(_ffmpegDir, "ffmpeg_dl.zip");

            progress.Report((0, "Connecting…"));

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(30);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Clipsy/1.0");

            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(
                    FFMPEG_URL, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                response = await http.GetAsync(
                    FFMPEG_URL_FALLBACK, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
            }

            using var _ = response;
            long? total = response.Content.Headers.ContentLength;

            progress.Report((2, "Downloading FFmpeg…"));

            await using (var fs = File.Create(zipPath))
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            {
                var buf = new byte[1 << 20];
                long done = 0;
                long lastMb = -1;
                int read;
                while ((read = await stream.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, read), ct);
                    done += read;
                    long mb = done / 1_048_576;
                    if (mb != lastMb) // throttle UI reports to once per MB
                    {
                        lastMb = mb;
                        int pct = total > 0 ? (int)(done * 88L / total.Value) : 0;
                        progress.Report((2 + pct, $"Downloading FFmpeg… {mb} MB"));
                    }
                }
            }

            ct.ThrowIfCancellationRequested();

            progress.Report((91, "Extracting…"));
            var extractDir = Path.Combine(_ffmpegDir, "_extract");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);
            File.Delete(zipPath);

            // Locate ffmpeg.exe anywhere inside the extracted tree
            var exe = FindFile(extractDir, "ffmpeg.exe");
            if (exe == null)
            {
                Directory.Delete(extractDir, true);
                progress.Report((100, "Failed – ffmpeg.exe not found in archive"));
                return false;
            }

            File.Copy(exe, _ffmpegExe, overwrite: true);
            Directory.Delete(extractDir, true);

            progress.Report((100, "Done"));
            return true;
        }
        catch (OperationCanceledException)
        {
            TryCleanup();
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipsy] FFmpeg download failed: {ex.Message}");
            TryCleanup();
            return false;
        }
    }

    public void Delete()
    {
        try { if (File.Exists(_ffmpegExe)) File.Delete(_ffmpegExe); }
        catch (Exception ex) { Debug.WriteLine($"[Clipsy] FFmpeg delete: {ex.Message}"); }
    }

    // ─── GIF conversion ───────────────────────────────────────────────────────
    // Used when ffmpeg.exe is present; otherwise the caller falls back to NativeGifEncoder.

    public async Task<bool> ConvertToGifAsync(string inputMp4, string outputGif)
    {
        if (!IsAvailable) return false;
        try
        {
            var s = SettingsService.Instance.Settings;
            var fps    = s.GifFps;
            var colors = Math.Clamp(s.GifColors, 4, 256);
            // dither param accepts named modes, not 0/1. The previous numeric
            // value made ffmpeg either bail out or produce a broken stream.
            var dither = s.GifDither ? "floyd_steinberg" : "none";

            // Single-pass split + palettegen + paletteuse. Avoids the fragile
            // two-pass palette file and works on every ffmpeg ≥4.x.
            var filter =
                $"fps={fps},split[a][b];" +
                $"[a]palettegen=max_colors={colors}[p];" +
                $"[b][p]paletteuse=dither={dither}";
            var args = $"-i \"{inputMp4}\" -filter_complex \"{filter}\" -y \"{outputGif}\"";
            return await RunAsync(args);
        }
        catch (Exception ex) { Debug.WriteLine($"[Clipsy] GIF: {ex.Message}"); return false; }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    public async Task<bool> RunAsync(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegExe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;

            // Drain both pipes or ffmpeg blocks when the OS pipe buffer fills.
            var drainOut = p.StandardOutput.ReadToEndAsync();
            var drainErr = p.StandardError.ReadToEndAsync();

            // Hard timeout guard so a wedged ffmpeg can never brick the app.
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                Debug.WriteLine("[Clipsy] FFmpeg run: timed out, killed");
                return false;
            }
            await Task.WhenAll(drainOut, drainErr);
            return p.ExitCode == 0;
        }
        catch (Exception ex) { Debug.WriteLine($"[Clipsy] FFmpeg run: {ex.Message}"); return false; }
    }

    private static string? FindFile(string dir, string name)
    {
        foreach (var f in Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories))
            return f;
        return null;
    }

    private void TryCleanup()
    {
        try
        {
            var zip = Path.Combine(_ffmpegDir, "ffmpeg_dl.zip");
            if (File.Exists(zip)) File.Delete(zip);
        }
        catch { }
    }
}
