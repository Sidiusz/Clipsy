using System;
using System.Collections.Generic;
using System.IO;
using ScreenRecorderLib;

namespace Clipsy.Services;

/// <summary>
/// Thin wrapper around ScreenRecorderLib that records the chosen region
/// to MP4. Region is supplied in virtual-screen pixel coordinates.
/// </summary>
public sealed class RecordingService : IDisposable
{
    private Recorder? _recorder;
    private DisplayRecordingSource? _source;
    private string _tempPath = string.Empty;

    public event Action<string>? RecordingComplete;
    public event Action<string>? RecordingFailed;
    public event Action<RecorderStatus>? StatusChanged;

    public string TempPath => _tempPath;

    public void Start(int x, int y, int width, int height)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Clipsy");
        Directory.CreateDirectory(tempDir);
        _tempPath = Path.Combine(tempDir, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        _source = new DisplayRecordingSource(DisplayRecordingSource.MainMonitor)
        {
            SourceRect = new ScreenRect(x, y, width, height),
            IsCursorCaptureEnabled = true,
            // Fill mode: any live region resize is rescaled into the fixed
            // OutputFrameSize, no letterbox / stuck-frame edges. Aspect may
            // distort if the user changes the region's ratio mid-recording —
            // a known trade-off because codec frame size is locked at start.
            Stretch = StretchMode.Fill,
        };

        var s = SettingsService.Instance.Settings;
        var (outW, outH) = ResolveOutputSize(s.VideoResolution, width, height);
        int bitrate = s.VideoBitrateMbps * 1_000_000;

        IVideoEncoder encoder = s.VideoCodec switch
        {
            "H.265" => new H265VideoEncoder(),
            _ => new H264VideoEncoder
            {
                BitrateMode = H264BitrateControlMode.Quality,
                EncoderProfile = H264Profile.High,
            },
        };

        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions
            {
                RecordingSources = new List<RecordingSourceBase> { _source },
            },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                OutputFrameSize = new ScreenSize(outW, outH),
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = encoder,
                Framerate = 30,
                Bitrate = bitrate,
                IsFixedFramerate = false,
                IsHardwareEncodingEnabled = true,
                IsThrottlingDisabled = false,
                IsLowLatencyEnabled = false,
                IsMp4FastStartEnabled = true,
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = true,
                IsInputDeviceEnabled = false,
                IsOutputDeviceEnabled = true,
                Bitrate = AudioBitrate.bitrate_128kbps,
                Channels = AudioChannels.Stereo,
            },
            MouseOptions = new MouseOptions
            {
                IsMousePointerEnabled = true,
                IsMouseClicksDetected = false,
            },
        };

        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnRecordingComplete += OnComplete;
        _recorder.OnRecordingFailed += OnFailed;
        _recorder.OnStatusChanged += OnStatus;
        _recorder.Record(_tempPath);
    }

    private static (int w, int h) ResolveOutputSize(string resolution, int captureW, int captureH)
    {
        int targetH = resolution switch
        {
            "480p" => 480, "720p" => 720, "1080p" => 1080, "1440p" => 1440, _ => 0
        };
        if (targetH == 0 || captureH <= targetH) return (captureW, captureH);
        double scale = (double)targetH / captureH;
        int w = Math.Max(2, (int)(captureW * scale) & ~1);
        int h = Math.Max(2, targetH & ~1);
        return (w, h);
    }

    private bool _paused;
    private bool _pendingRegionWhilePaused;

    public void Pause() { _paused = true; _recorder?.Pause(); }
    public void Resume()
    {
        _paused = false;
        _recorder?.Resume();
        // While paused, ScreenRecorderLib silently ignores
        // GetDynamicOptionsBuilder().Apply() — the new SourceRect is stored
        // on _source but never reaches the encoder. Replay it on resume.
        if (_pendingRegionWhilePaused)
        {
            _pendingRegionWhilePaused = false;
            ApplyCurrentSourceRect();
        }
    }
    public void Stop() => _recorder?.Stop();


    public void UpdateRegion(int x, int y, int width, int height)
    {
        if (_source == null || _recorder == null) return;
        _source.SourceRect = new ScreenRect(x, y, width, height);
        if (_paused)
        {
            // Defer; dynamic-options Apply() during pause is a no-op.
            _pendingRegionWhilePaused = true;
            return;
        }
        ApplyCurrentSourceRect();
    }

    private void ApplyCurrentSourceRect()
    {
        if (_source == null || _recorder == null) return;
        try
        {
            var builder = _recorder.GetDynamicOptionsBuilder();
            builder.SetUpdatedRecordingSource(_source);
            builder.Apply();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] UpdateRegion dynamic apply failed: {ex.Message}");
        }
    }

    public RecorderStatus Status => _recorder?.Status ?? RecorderStatus.Idle;

    private void OnComplete(object? sender, RecordingCompleteEventArgs e)
    {
        RecordingComplete?.Invoke(e.FilePath);
    }

    private void OnFailed(object? sender, RecordingFailedEventArgs e)
    {
        RecordingFailed?.Invoke(e.Error);
    }

    private void OnStatus(object? sender, RecordingStatusEventArgs e)
    {
        StatusChanged?.Invoke(e.Status);
    }

    public void Dispose()
    {
        if (_recorder != null)
        {
            _recorder.OnRecordingComplete -= OnComplete;
            _recorder.OnRecordingFailed -= OnFailed;
            _recorder.OnStatusChanged -= OnStatus;
            _recorder.Dispose();
            _recorder = null;
            _source = null;
        }
    }
}
