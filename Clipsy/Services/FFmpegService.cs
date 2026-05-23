using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <summary>
/// FFmpeg download, management, and conversion service.
/// All VP9 / AV1 recording paths use FFmpegRecordingService;
/// this class owns the binary lifecycle and GIF conversion.
/// </summary>
public sealed class FFmpegService
{
    private static readonly Lazy<FFmpegService> _instance = new(() => new FFmpegService());
    public static FFmpegService Instance => _instance.Value;

    private readonly string _ffmpegDir;
    private readonly string _ffmpegExe;

    // gyan.dev latest essentials release (Windows x64, ~100 MB zip)
    private const string FFMPEG_URL =
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
            http.Timeout = TimeSpan.FromMinutes(15);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Clipsy/1.0");

            using var response = await http.GetAsync(
                FFMPEG_URL, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long? total = response.Content.Headers.ContentLength;

            progress.Report((2, "Downloading FFmpeg…"));

            await using (var fs = File.Create(zipPath))
            await using (var stream = await response.Content.ReadAsStreamAsync(ct))
            {
                var buf = new byte[65536];
                long done = 0;
                int read;
                while ((read = await stream.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0)
                    {
                        int pct = (int)(done * 88L / total.Value);
                        long mb = done / 1_048_576;
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

    // ─── GIF conversion (unchanged logic) ────────────────────────────────────

    public async Task<bool> ConvertToGifAsync(string inputMp4, string outputGif)
    {
        if (!IsAvailable) return false;
        try
        {
            var s = SettingsService.Instance.Settings;
            var fps    = s.GifFps;
            var colors = s.GifColors;
            var dither = s.GifDither ? "1" : "0";

            var palette = $"{outputGif}.palette.png";
            var r1 = await RunAsync(
                $"-i \"{inputMp4}\" -vf \"fps={fps},scale=-1:-1:flags=lanczos,palettegen=max_colors={colors}\" -y \"{palette}\"");
            if (!r1) return false;

            var r2 = await RunAsync(
                $"-i \"{inputMp4}\" -i \"{palette}\" -lavfi \"fps={fps},scale=-1:-1:flags=lanczos[x];[x][1:v]paletteuse=dither={dither}\" -y \"{outputGif}\"");
            try { File.Delete(palette); } catch { }
            return r2;
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
            await p.WaitForExitAsync();
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
