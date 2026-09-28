using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <summary>ffmpeg binary lifecycle (verified download) plus the conversions that use it:
/// GIF export, container remux and VP9/AV1 transcode of finished recordings.</summary>
public sealed class FFmpegService
{
    private static readonly Lazy<FFmpegService> _instance = new(() => new FFmpegService());
    public static FFmpegService Instance => _instance.Value;

    // Versioned release (never re-tagged) with the digest GitHub reports for the asset.
    private const string DownloadUrl =
        "https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip";
    private const string DownloadSha256 = "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba";

    private readonly string _ffmpegDir;
    private readonly string _ffmpegExe;
    private HashSet<string>? _encoders;

    private FFmpegService()
    {
        _ffmpegDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Clipsy", "ffmpeg");
        _ffmpegExe = Path.Combine(_ffmpegDir, "ffmpeg.exe");
    }

    public string ExePath => _ffmpegExe;
    public bool IsAvailable => File.Exists(_ffmpegExe);

    /// <summary>Downloads and verifies ffmpeg.exe. Reports (0-100, message). Cancellable.</summary>
    public Task<bool> DownloadAsync(IProgress<(int Percent, string Message)> progress, CancellationToken ct = default)
        => Task.Run(() => DownloadCoreAsync(progress, ct), ct);

    private async Task<bool> DownloadCoreAsync(IProgress<(int Percent, string Message)> progress, CancellationToken ct)
    {
        var zipPath = Path.Combine(_ffmpegDir, "ffmpeg_dl.zip");
        var extractDir = Path.Combine(_ffmpegDir, "_extract");
        var staged = _ffmpegExe + ".new";
        try
        {
            Directory.CreateDirectory(_ffmpegDir);
            progress.Report((0, "Connecting…"));

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Clipsy");
            using (var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                progress.Report((2, "Downloading FFmpeg…"));

                await using var fs = File.Create(zipPath);
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                var buf = new byte[1 << 20];
                long done = 0, lastMb = -1;
                int read;
                while ((read = await stream.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, read), ct);
                    done += read;
                    long mb = done / 1_048_576;
                    if (mb == lastMb) continue; // throttle UI reports to once per MB
                    lastMb = mb;
                    int pct = total > 0 ? (int)(done * 86L / total.Value) : 0;
                    progress.Report((2 + pct, $"Downloading FFmpeg… {mb} MB"));
                }
            }

            progress.Report((89, "Verifying…"));
            await using (var zip = File.OpenRead(zipPath))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(zip, ct)).ToLowerInvariant();
                if (hash != DownloadSha256)
                    throw new InvalidDataException($"FFmpeg download failed verification (sha256 {hash}).");
            }

            progress.Report((92, "Extracting…"));
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            ZipFile.ExtractToDirectory(zipPath, extractDir);
            var exe = Directory.EnumerateFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new FileNotFoundException("ffmpeg.exe not found in archive");

            // Stage then rename, so an interrupted copy never leaves a truncated ffmpeg.exe behind.
            File.Copy(exe, staged, overwrite: true);
            File.Move(staged, _ffmpegExe, overwrite: true);
            _encoders = null;
            progress.Report((100, "Done"));
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("FFmpeg download failed", ex);
            return false;
        }
        finally
        {
            TryDeleteFile(zipPath);
            TryDeleteFile(staged);
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { }
        }
    }

    public void Delete()
    {
        try { if (File.Exists(_ffmpegExe)) File.Delete(_ffmpegExe); _encoders = null; }
        catch (Exception ex) { Diagnostics.Log($"FFmpeg delete: {ex.Message}"); }
    }

    // ─── Conversions ─────────────────────────────────────────────────────────

    /// <summary>Two-pass palette GIF: the palette is a small file, so long clips never sit in memory.</summary>
    public async Task<bool> ConvertToGifAsync(string input, string outputGif, CancellationToken ct = default)
    {
        if (!IsAvailable) return false;
        var s = SettingsService.Instance.Settings;
        int fps = s.GifFps;
        int colors = Math.Clamp(s.GifColors, 4, 256);
        var dither = s.GifDither ? "floyd_steinberg" : "none";
        var palette = Path.Combine(Path.GetTempPath(), "Clipsy", $"palette_{Guid.NewGuid():N}.png");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(palette)!);
            if (!await RunAsync(new[] { "-i", input, "-vf", $"fps={fps},palettegen=max_colors={colors}:stats_mode=diff", "-y", palette }, ct: ct))
                return false;
            return await RunAsync(new[] { "-i", input, "-i", palette,
                "-lavfi", $"fps={fps}[x];[x][1:v]paletteuse=dither={dither}:diff_mode=rectangle", "-y", outputGif }, ct: ct);
        }
        finally { TryDeleteFile(palette); }
    }

    public Task<bool> RemuxAsync(string input, string output, CancellationToken ct = default)
        => RunAsync(new[] { "-i", input, "-map", "0", "-c", "copy", "-y", output }, ct: ct);

    /// <summary>Re-encodes a finished H.264 recording to VP9 or AV1 (Opus audio) in MKV.</summary>
    public async Task<bool> TranscodeAsync(string input, string output, string codec, int bitrateMbps, CancellationToken ct = default)
    {
        if (!IsAvailable) return false;
        var encoders = await GetEncodersAsync(ct);
        var args = new List<string> { "-i", input, "-map", "0" };
        if (codec == "AV1")
        {
            if (encoders.Contains("libsvtav1")) args.AddRange(new[] { "-c:v", "libsvtav1", "-preset", "8" });
            else if (encoders.Contains("libaom-av1")) args.AddRange(new[] { "-c:v", "libaom-av1", "-usage", "realtime", "-cpu-used", "8", "-row-mt", "1" });
            else return false;
        }
        else
        {
            if (!encoders.Contains("libvpx-vp9")) return false;
            args.AddRange(new[] { "-c:v", "libvpx-vp9", "-deadline", "realtime", "-cpu-used", "8", "-row-mt", "1" });
        }
        args.AddRange(new[] { "-b:v", $"{Math.Clamp(bitrateMbps, 1, 50)}M" });
        args.AddRange(encoders.Contains("libopus") ? new[] { "-c:a", "libopus", "-b:a", "128k" } : new[] { "-c:a", "copy" });
        args.AddRange(new[] { "-y", output });
        return await RunAsync(args, ct: ct);
    }

    private async Task<HashSet<string>> GetEncodersAsync(CancellationToken ct)
    {
        if (_encoders != null) return _encoders;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = await RunCaptureAsync(new[] { "-hide_banner", "-encoders" }, ct);
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6) set.Add(parts[1]);
        }
        return _encoders = set;
    }

    // ─── Process runner ──────────────────────────────────────────────────────

    /// <summary>Runs ffmpeg with argument-list quoting; no timeout unless given (long clips take long).</summary>
    public async Task<bool> RunAsync(IEnumerable<string> arguments, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        try
        {
            var (code, stderr) = await RunProcessAsync(arguments, timeout, ct);
            if (code != 0) Diagnostics.Log($"ffmpeg exit {code}: {Tail(stderr)}");
            return code == 0;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { Diagnostics.Log("ffmpeg run failed", ex); return false; }
    }

    private async Task<string> RunCaptureAsync(IEnumerable<string> arguments, CancellationToken ct)
    {
        try
        {
            var psi = CreateStartInfo(arguments);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
            ChildProcessJob.Assign(p);
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return await stdout + await stderr;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Log("ffmpeg query failed", ex);
            return string.Empty;
        }
    }

    private async Task<(int ExitCode, string Stderr)> RunProcessAsync(IEnumerable<string> arguments, TimeSpan? timeout, CancellationToken ct)
    {
        using var p = Process.Start(CreateStartInfo(arguments)) ?? throw new InvalidOperationException("ffmpeg did not start");
        ChildProcessJob.Assign(p);
        // Drain both pipes or ffmpeg blocks once the OS pipe buffer fills.
        var drainOut = p.StandardOutput.ReadToEndAsync();
        var drainErr = p.StandardError.ReadToEndAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        await Task.WhenAll(drainOut, drainErr);
        return (p.ExitCode, drainErr.Result);
    }

    private ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-nostdin");
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        return psi;
    }

    private static string Tail(string s) => s.Length <= 600 ? s.Trim() : s[^600..].Trim();

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
