using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <summary>
/// Direct FFmpeg screen recording service for VP9/AV1 codecs.
/// Records screen region directly to VP9/AV1 without intermediate conversion.
/// </summary>
public static class FFmpegRecordingService
{
    public static async Task<bool> RecordScreenAsync(
        string outputPath,
        int x, int y, int width, int height,
        string codec, int bitrateMbps,
        Action<string>? onComplete = null,
        Action<string>? onError = null)
    {
        if (!FFmpegService.Instance.IsAvailable)
            return false;

        try
        {
            var ffmpegPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Clipsy", "ffmpeg", "bin", "ffmpeg.exe");

            var args = BuildRecordingArgs(outputPath, x, y, width, height, codec, bitrateMbps);

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };

            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    System.Diagnostics.Debug.WriteLine($"[FFmpeg] {e.Data}");
                }
            };

            process.Start();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            if (process.ExitCode == 0 && File.Exists(outputPath))
            {
                onComplete?.Invoke(outputPath);
                return true;
            }
            else
            {
                onError?.Invoke($"FFmpeg recording failed with exit code {process.ExitCode}");
                return false;
            }
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
            return false;
        }
    }

    private static string BuildRecordingArgs(string output, int x, int y, int width, int height, string codec, int bitrateMbps)
    {
        var videoCodec = codec.ToLowerInvariant() switch
        {
            "vp9" => "libvpx-vp9",
            "av1" => "libaom-av1",
            _ => "libx264"
        };

        // Windows screen capture using gdigrab
        var args = $"-f gdigrab -framerate 30 -offset_x {x} -offset_y {y} -video_size {width}x{height} -i desktop";
        args += $" -c:v {videoCodec} -b:v {bitrateMbps}M";

        // Audio capture
        args += " -f dshow -i audio=\"virtual-audio-capturer\"";
        args += " -c:a aac -b:a 128k";

        // Output settings
        args += " -pix_fmt yuv420p -preset medium -crf 23";
        args += $" -y \"{output}\"";

        return args;
    }
}