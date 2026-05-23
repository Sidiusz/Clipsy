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

    private bool _usingFFmpeg;

    public void Start(int x, int y, int width, int height)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Clipsy");
        Directory.CreateDirectory(tempDir);
        _tempPath = Path.Combine(tempDir, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        var s = SettingsService.Instance.Settings;
        bool useFFmpegRecording = (s.VideoCodec == "VP9" || s.VideoCodec == "AV1") && FFmpegService.Instance.IsAvailable;

        if (useFFmpegRecording)
        {
            StartFFmpegRecording(x, y, width, height);
            return;
        }

        StartScreenRecorderLibRecording(x, y, width, height);
    }

    private async void StartFFmpegRecording(int x, int y, int width, int height)
    {
        _usingFFmpeg = true;
        var s = SettingsService.Instance.Settings;

        var success = await FFmpegRecordingService.RecordScreenAsync(
            _tempPath, x, y, width, height, s.VideoCodec, s.VideoBitrateMbps,
            onComplete: (path) => RecordingComplete?.Invoke(path),
            onError: (error) => RecordingFailed?.Invoke(error)
        );

        if (!success)
        {
            RecordingFailed?.Invoke("FFmpeg recording failed to start");
        }
    }

    private void StartScreenRecorderLibRecording(int x, int y, int width, int height)
    {
        _usingFFmpeg = false;
        var s = SettingsService.Instance.Settings;

        _source = new DisplayRecordingSource(DisplayRecordingSource.MainMonitor)
        {
            SourceRect = new ScreenRect(x, y, width, height),
            IsCursorCaptureEnabled = true,
            Stretch = StretchMode.Fill,
        };

        var (outW, outH) = ResolveOutputSize(s.VideoResolution, width, height);
        int bitrate = s.VideoBitrateMbps * 1_000_000;

        IVideoEncoder encoder;
        switch (s.VideoCodec)
        {
            case "H.265":
                encoder = new H265VideoEncoder();
                break;
            case "VP9":
            case "AV1":
                throw new InvalidOperationException($"{s.VideoCodec} requires FFmpeg");
            default: // H.264
                encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.Quality,
                    EncoderProfile = H264Profile.High,
                };
                break;
        }

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
