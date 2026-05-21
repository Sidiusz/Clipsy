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
                OutputFrameSize = new ScreenSize(width, height),
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.Quality,
                    EncoderProfile = H264Profile.High,
                },
                Framerate = 30,
                Bitrate = 8_000_000,
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

    public void Pause() => _recorder?.Pause();
    public void Resume() => _recorder?.Resume();
    public void Stop() => _recorder?.Stop();


    public void UpdateRegion(int x, int y, int width, int height)
    {
        if (_source == null) return;
        _source.SourceRect = new ScreenRect(x, y, width, height);
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
