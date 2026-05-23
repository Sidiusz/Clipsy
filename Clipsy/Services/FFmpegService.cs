using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <summary>
/// FFmpeg download, verification, and video conversion service.
/// Downloads FFmpeg to app directory, verifies hash, converts MP4 to other formats.
/// </summary>
public sealed class FFmpegService
{
    private static readonly Lazy<FFmpegService> _instance = new(() => new FFmpegService());
    public static FFmpegService Instance => _instance.Value;

    private readonly string _ffmpegDir;
    private readonly string _ffmpegExe;

    // FFmpeg 7.0.2 Windows x64 essentials build from https://www.gyan.dev/ffmpeg/builds/
    private const string FFMPEG_URL = "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-7.0.2-essentials_build.zip";
    private const string FFMPEG_SHA256 = "b4bb2c3c6c8e5b4c8b4b8b4b8b4b8b4b8b4b8b4b8b4b8b4b8b4b8b4b8b4b8b4b"; // TODO: Replace with actual hash

    public event Action<string>? DownloadProgress;
    public event Action<bool>? DownloadComplete;

    private FFmpegService()
    {
        _ffmpegDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipsy", "ffmpeg");
        _ffmpegExe = Path.Combine(_ffmpegDir, "bin", "ffmpeg.exe");
    }

    public bool IsAvailable => File.Exists(_ffmpegExe);

    public async Task<bool> DownloadAsync()
    {
        try
        {
            DownloadProgress?.Invoke("Starting download...");

            Directory.CreateDirectory(_ffmpegDir);
            var zipPath = Path.Combine(_ffmpegDir, "ffmpeg.zip");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);

            DownloadProgress?.Invoke("Downloading FFmpeg...");
            var response = await client.GetAsync(FFMPEG_URL);
            response.EnsureSuccessStatusCode();

            await using var fs = File.Create(zipPath);
            await response.Content.CopyToAsync(fs);

            DownloadProgress?.Invoke("Verifying download...");
            if (!await VerifyHashAsync(zipPath))
            {
                File.Delete(zipPath);
                DownloadComplete?.Invoke(false);
                return false;
            }

            DownloadProgress?.Invoke("Extracting...");
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, _ffmpegDir, overwriteFiles: true);
            File.Delete(zipPath);

            // Move from extracted folder structure to direct bin folder
            var extractedDir = Directory.GetDirectories(_ffmpegDir, "ffmpeg-*")[0];
            var binSrc = Path.Combine(extractedDir, "bin");
            var binDest = Path.Combine(_ffmpegDir, "bin");

            if (Directory.Exists(binDest))
                Directory.Delete(binDest, recursive: true);
            Directory.Move(binSrc, binDest);
            Directory.Delete(extractedDir, recursive: true);

            DownloadProgress?.Invoke("Download complete!");
            DownloadComplete?.Invoke(true);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] FFmpeg download failed: {ex.Message}");
            DownloadComplete?.Invoke(false);
            return false;
        }
    }

    private async Task<bool> VerifyHashAsync(string filePath)
    {
        try
        {
            using var sha256 = SHA256.Create();
            await using var stream = File.OpenRead(filePath);
            var hash = await sha256.ComputeHashAsync(stream);
            var hashString = Convert.ToHexString(hash).ToLowerInvariant();
            return hashString == FFMPEG_SHA256;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> ConvertToGifAsync(string inputMp4, string outputGif)
    {
        if (!IsAvailable) return false;

        try
        {
            var settings = SettingsService.Instance.Settings;
            var fps = settings.GifFps;
            var colors = settings.GifColors;
            var dither = settings.GifDither ? "1" : "0";

            // FFmpeg command for high-quality GIF conversion with palette optimization
            var args = $"-i \"{inputMp4}\" -vf \"fps={fps},scale=-1:-1:flags=lanczos,palettegen=max_colors={colors}\" -y \"{outputGif}.palette.png\"";

            var paletteResult = await RunFFmpegAsync(args);
            if (!paletteResult) return false;

            args = $"-i \"{inputMp4}\" -i \"{outputGif}.palette.png\" -lavfi \"fps={fps},scale=-1:-1:flags=lanczos[x];[x][1:v]paletteuse=dither={dither}\" -y \"{outputGif}\"";

            var result = await RunFFmpegAsync(args);

            // Cleanup palette file
            try { File.Delete($"{outputGif}.palette.png"); } catch { }

            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] GIF conversion failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ConvertFormatAsync(string inputMp4, string outputPath, string format, string codec = "")
    {
        if (!IsAvailable) return false;

        try
        {
            string args = format.ToLowerInvariant() switch
            {
                "avi" => $"-i \"{inputMp4}\" -c copy \"{outputPath}\"",
                "mkv" => $"-i \"{inputMp4}\" -c copy \"{outputPath}\"",
                "gif" => throw new InvalidOperationException("Use ConvertToGifAsync for GIF conversion"),
                "mp4" => BuildMp4Args(inputMp4, outputPath, codec),
                _ => $"-i \"{inputMp4}\" -c copy \"{outputPath}\""
            };

            return await RunFFmpegAsync(args);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Format conversion failed: {ex.Message}");
            return false;
        }
    }

    private string BuildMp4Args(string input, string output, string codec)
    {
        var settings = SettingsService.Instance.Settings;
        var bitrate = settings.VideoBitrateMbps;

        return codec.ToLowerInvariant() switch
        {
            "vp9" => $"-i \"{input}\" -c:v libvpx-vp9 -b:v {bitrate}M -c:a libopus \"{output}\"",
            "av1" => $"-i \"{input}\" -c:v libaom-av1 -b:v {bitrate}M -c:a libopus \"{output}\"",
            "h.265" => $"-i \"{input}\" -c:v libx265 -b:v {bitrate}M -c:a aac \"{output}\"",
            _ => $"-i \"{input}\" -c:v libx264 -b:v {bitrate}M -c:a aac \"{output}\""
        };
    }

    private async Task<bool> RunFFmpegAsync(string arguments)
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
                RedirectStandardError = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] FFmpeg execution failed: {ex.Message}");
            return false;
        }
    }
}