using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <summary>
/// Records screen directly to VP9 or AV1 using FFmpeg's gdigrab + wasapi loopback.
/// Same event/method surface as RecordingService so RecordingController can use both.
/// </summary>
public sealed class FFmpegRecordingService : IDisposable
{
    private Process?  _process;
    private string    _tempPath  = string.Empty;
    private bool      _stopping;

    public event Action<string>? RecordingComplete;
    public event Action<string>? RecordingFailed;

    public string TempPath => _tempPath;

    // ─── Start ───────────────────────────────────────────────────────────────

    public void Start(int x, int y, int w, int h)
    {
        if (!FFmpegService.Instance.IsAvailable)
        {
            RecordingFailed?.Invoke("FFmpeg not found.");
            return;
        }

        var s      = SettingsService.Instance.Settings;
        var codec  = s.VideoCodec;          // "VP9" or "AV1"
        var kbps   = s.VideoBitrateMbps;

        var tempDir = Path.Combine(Path.GetTempPath(), "Clipsy");
        Directory.CreateDirectory(tempDir);
        _tempPath = Path.Combine(tempDir, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.mkv");

        bool micEnabled = s.MicrophoneEnabled && !s.MicrophoneMuted;
        string? micDevice = micEnabled && !string.IsNullOrEmpty(s.MicrophoneDevice) ? s.MicrophoneDevice : null;
        int fps = RecordingService.ResolveFramerate(s.VideoFramerate);
        var args = BuildArgs(x, y, w, h, codec, kbps, fps, _tempPath, withAudio: true, micEnabled: micEnabled, micFriendlyName: micDevice);
        _process = Launch(args);

        if (_process == null)
        {
            RecordingFailed?.Invoke("Failed to start FFmpeg process.");
        }
    }

    // ─── Stop / Pause / Resume / UpdateRegion ────────────────────────────────

    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        try
        {
            if (_process is { HasExited: false })
            {
                // Send 'q' – ffmpeg flushes and writes the file trailer cleanly
                _process.StandardInput.WriteLine("q");
                _process.StandardInput.Flush();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipsy] FFmpegRecordingService.Stop: {ex.Message}");
        }
    }

    /// <summary>Pause is not supported for gdigrab; recording continues uninterrupted.</summary>
    public void Pause() { }
    public void Resume() { }

    /// <summary>FFmpeg cannot change region mid-recording; call is ignored.</summary>
    public void UpdateRegion(int x, int y, int w, int h) { }

    // ─── Internals ───────────────────────────────────────────────────────────

    private Process? Launch(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = FFmpegService.Instance.ExePath,
            Arguments              = args,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            RedirectStandardInput  = true,   // for 'q' quit signal
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };

        try
        {
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.Exited += OnExited;
            p.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Debug.WriteLine($"[FFmpeg] {e.Data}");
            };
            p.Start();
            p.BeginErrorReadLine();
            return p;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipsy] FFmpegRecordingService.Launch: {ex.Message}");
            return null;
        }
    }

    private static string BuildArgs(
        int x, int y, int w, int h,
        string codec, int bitrateMbps, int fps, string output,
        bool withAudio, bool micEnabled = false, string? micFriendlyName = null)
    {
        // ── Video encoder flags ──────────────────────────────────────────────
        string videoEncoder = codec switch
        {
            "AV1" => "libaom-av1 -usage realtime -cpu-used 8",
            _     => "libvpx-vp9 -deadline realtime -cpu-used 8 -row-mt 1",
        };

        // ── Inputs ──────────────────────────────────────────────────────────
        // gdigrab: Windows GDI screen capture (includes DWM-composited output)
        var sb = new System.Text.StringBuilder();
        sb.Append($"-f gdigrab -framerate {fps} -offset_x {x} -offset_y {y}");
        sb.Append($" -video_size {w}x{h} -draw_mouse 1 -i desktop");

        bool hasMic = withAudio && micEnabled;
        if (withAudio)
        {
            // wasapi loopback: system audio (no extra drivers required on Win10+)
            sb.Append(" -f wasapi -loopback -i \"\"");
        }
        if (hasMic)
        {
            // Mic via WASAPI capture device. FFmpeg WASAPI accepts the
            // MMDevice endpoint ID (DeviceName) directly.
            string micId = string.IsNullOrEmpty(micFriendlyName) ? "" : micFriendlyName;
            sb.Append($" -f wasapi -i \"{micId}\"");
        }

        // ── Encoding ────────────────────────────────────────────────────────
        sb.Append($" -c:v {videoEncoder} -b:v {bitrateMbps}M -r {fps}");

        if (withAudio && hasMic)
        {
            // Mix system audio (input 1) + microphone (input 2) into one track.
            sb.Append(" -filter_complex \"[1:a][2:a]amix=inputs=2:duration=first[aout]\"");
            sb.Append(" -map 0:v -map \"[aout]\" -c:a libopus -b:a 128k");
        }
        else if (withAudio)
        {
            sb.Append(" -c:a libopus -b:a 128k");
        }

        sb.Append($" -y \"{output}\"");
        return sb.ToString();
    }

    private void OnExited(object? sender, EventArgs e)
    {
        var path     = _tempPath;
        var exitCode = _process?.ExitCode ?? -1;

        bool hasFile = File.Exists(path) && new FileInfo(path).Length > 0;

        if (hasFile)
        {
            // 'q' quit produces exit code 255 on Windows; that's fine.
            RecordingComplete?.Invoke(path);
        }
        else
        {
            RecordingFailed?.Invoke(
                $"FFmpeg exited with code {exitCode} and produced no output file.");
        }
    }

    // ─── IDisposable ─────────────────────────────────────────────────────────

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
            _process?.Dispose();
            _process = null;
        }
        catch { }
    }
}
