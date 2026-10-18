using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ScreenRecorderLib;

namespace Clipsy.Services;

/// <summary>ScreenRecorderLib wrapper recording the chosen region (virtual-screen
/// pixel coords) to MP4.</summary>
public sealed class RecordingService : IDisposable
{
    private Recorder? _recorder;
    // Register every monitor once so a live region can move/resize across
    // displays without trying to add recording sources after the encoder starts.
    private readonly List<DisplayRecordingSource> _sources = new();
    private readonly List<Rectangle> _sourceMonitors = new(); // parallel to _sources
    private Rectangle _sourceCanvasBounds;
    private Rectangle _activeRegion;
    private string _tempPath = string.Empty;

    public event Action<string>? RecordingComplete;
    public event Action<string>? RecordingFailed;

    public string TempPath => _tempPath;

    public static string TempDirectory => Path.Combine(Path.GetTempPath(), "Clipsy");

    public void Start(int x, int y, int width, int height, string? overrideCodec = null)
    {
        PrepareTempPath();
        bool cursor = SettingsService.Instance.Settings.CaptureVideoCursor;
        BuildSources(x, y, width, height, cursor);
        _activeRegion = new Rectangle(x, y, width, height);
        StartRecorder(_sources.Cast<RecordingSourceBase>().ToList(), width, height, GetCanvasCrop(_activeRegion),
            overrideCodec, systemAudio: true, cursor);
    }

    /// <summary>Records one window wherever it moves. With <paramref name="audioProcessId"/> the system mix is
    /// replaced by that process tree's audio, written to <see cref="AppAudioPath"/> for muxing on save.</summary>
    public void StartWindow(IntPtr hwnd, int width, int height, int? audioProcessId, string? overrideCodec = null)
    {
        PrepareTempPath();
        bool cursor = SettingsService.Instance.Settings.CaptureVideoCursor;
        var source = new WindowRecordingSource(hwnd)
        {
            IsBorderRequired = false,
            IsCursorCaptureEnabled = cursor,
            Stretch = StretchMode.Uniform,
        };
        if (audioProcessId is int pid)
        {
            try { _appAudio = ProcessAudioCapture.Start(pid, Path.ChangeExtension(_tempPath, ".pcm")); }
            catch (Exception ex)
            {
                Diagnostics.Log($"App audio capture for pid {pid} failed; recording system audio", ex);
                AppAudioFailed = true;
            }
        }
        try
        {
            StartRecorder(new List<RecordingSourceBase> { source }, width, height, null, overrideCodec, systemAudio: _appAudio == null, cursor);
        }
        catch
        {
            DisposeAppAudio();
            throw;
        }
    }

    /// <summary>Raw app audio recorded next to the video (window recordings), or null.</summary>
    public string? AppAudioPath => _appAudio?.OutputPath;

    /// <summary>App-only audio was asked for but Windows refused it; the system mix is recorded instead.</summary>
    public bool AppAudioFailed { get; private set; }

    /// <summary>The recording has its own audio track (system mix or microphone).</summary>
    public bool HasAudioTrack { get; private set; }

    private ProcessAudioCapture? _appAudio;

    private void PrepareTempPath()
    {
        Directory.CreateDirectory(TempDirectory);
        _tempPath = Path.Combine(TempDirectory, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.mp4");
    }

    private void StartRecorder(List<RecordingSourceBase> sources, int width, int height, ScreenRect? sourceRect,
        string? overrideCodec, bool systemAudio, bool cursor)
    {
        var s = SettingsService.Instance.Settings;
        var (outW, outH) = ResolveOutputSize(s.VideoResolution, width, height);
        int bitrate = s.VideoBitrateMbps * 1_000_000;

        // Quality mode ignores Bitrate, which made the bitrate setting a no-op; H.265 has no VBR mode.
        IVideoEncoder encoder = (overrideCodec ?? s.VideoCodec) switch
        {
            "H.265" => new H265VideoEncoder { BitrateMode = H265BitrateControlMode.CBR },
            _ => new H264VideoEncoder
            {
                BitrateMode = H264BitrateControlMode.UnconstrainedVBR,
                EncoderProfile = H264Profile.High,
            },
        };
        bool mic = s.MicrophoneEnabled;
        HasAudioTrack = mic || systemAudio;

        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = sources },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                OutputFrameSize = new ScreenSize(outW, outH),
                SourceRect = sourceRect,
                Stretch = StretchMode.Uniform,
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = encoder,
                Framerate = ResolveFramerate(s.VideoFramerate),
                Bitrate = bitrate,
                IsFixedFramerate = false,
                IsHardwareEncodingEnabled = true,
                IsThrottlingDisabled = false,
                IsLowLatencyEnabled = false,
                IsMp4FastStartEnabled = true,
            },
            // The input device is opened whenever the mic feature is on; mute is volume 0, so
            // unmuting mid-recording works even if the recording started muted.
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = HasAudioTrack,
                IsInputDeviceEnabled = mic,
                InputVolume = mic && !s.MicrophoneMuted ? 1f : 0f,
                AudioInputDevice = mic ? ResolveInputDevice(s.MicrophoneDevice) : null,
                IsOutputDeviceEnabled = systemAudio,
                Bitrate = AudioBitrate.bitrate_128kbps,
                Channels = AudioChannels.Stereo,
            },
            MouseOptions = new MouseOptions
            {
                IsMousePointerEnabled = cursor,
                IsMouseClicksDetected = false,
            },
        };

        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnRecordingComplete += OnComplete;
        _recorder.OnRecordingFailed += OnFailed;
        _recorder.OnStatusChanged += OnStatusChanged;
        _recorder.Record(_tempPath);
    }

    // The app audio follows the encoder's own timeline, so pauses and a slow start stay in sync.
    private void OnStatusChanged(object? sender, RecordingStatusEventArgs e)
    {
        if (e.Status == RecorderStatus.Recording) _appAudio?.Resume();
        else _appAudio?.Pause();
    }

    private void DisposeAppAudio()
    {
        try { _appAudio?.Dispose(); }
        catch (Exception ex) { Diagnostics.Log("App audio dispose failed", ex); }
    }

    // A saved device that's gone (unplugged headset) would fail the whole recording; use the default.
    private static string? ResolveInputDevice(string? saved)
    {
        if (string.IsNullOrEmpty(saved)) return null;
        try
        {
            foreach (var d in Recorder.GetSystemAudioDevices(AudioDeviceSource.InputDevices))
                if (string.Equals(d.DeviceName, saved, StringComparison.OrdinalIgnoreCase)) return saved;
            Diagnostics.Log("Recording: saved microphone not found; using the default device.");
        }
        catch (Exception ex) { Diagnostics.Log("Recording: audio device query failed", ex); }
        return null;
    }

    // Each monitor occupies its physical position inside one virtual-desktop canvas.
    // Sources outside the current region are disabled and re-enabled dynamically.
    private void BuildSources(int rx, int ry, int rw, int rh, bool cursor)
    {
        _sources.Clear();
        _sourceMonitors.Clear();
        _sourceCanvasBounds = ScreenFreezeService.GetVirtualScreenBounds();
        var region = new Rectangle(rx, ry, rw, rh);
        foreach (var (name, bounds) in EnumerateMonitors())
        {
            bool active = Intersects(region, bounds);
            var source = new DisplayRecordingSource(name)
            {
                SourceRect = new ScreenRect(0, 0, bounds.Width, bounds.Height),
                Position = new ScreenPoint(bounds.X - _sourceCanvasBounds.X, bounds.Y - _sourceCanvasBounds.Y),
                IsVideoCaptureEnabled = active,
                IsCursorCaptureEnabled = cursor,
                Stretch = StretchMode.None,
            };
            _sources.Add(source);
            _sourceMonitors.Add(bounds);
        }
        if (_sources.Count == 0)
            throw new InvalidOperationException("No display recording sources are available.");
    }

    private static IEnumerable<(string DeviceName, Rectangle Bounds)> EnumerateMonitors()
    {
        var list = new List<(string, Rectangle)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                var r = new Rectangle(mi.rcMonitor.left, mi.rcMonitor.top,
                    mi.rcMonitor.right - mi.rcMonitor.left, mi.rcMonitor.bottom - mi.rcMonitor.top);
                list.Add((mi.szDevice, r));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Maps the VideoFramerate setting to fps; 0 = native (primary
    /// display's refresh rate).</summary>
    public static int ResolveFramerate(int setting)
    {
        if (setting > 0) return Math.Clamp(setting, 10, 240);
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmDisplayFrequency > 1)
            return Math.Clamp(dm.dmDisplayFrequency, 10, 240);
        return 60;
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
    private bool? _micMuted; // last requested mute state; null = never toggled

    public void Pause() { _paused = true; _recorder?.Pause(); }
    public void Resume()
    {
        _paused = false;
        // Paused ScreenRecorderLib ignores Apply(), so a mute toggled while paused would never
        // reach the encoder and the mic would go live on resume. Sent before too, in case it's honored.
        if (_micMuted is bool before) ApplyMicMuted(before);
        _recorder?.Resume();
        if (_micMuted is bool after) ApplyMicMuted(after);
        // Same for the new SourceRect: replay it on resume.
        if (_pendingRegionWhilePaused)
        {
            _pendingRegionWhilePaused = false;
            ApplyCurrentRegion();
        }
    }
    public void Stop() => _recorder?.Stop();

    public void SetMicMuted(bool muted)
    {
        _micMuted = muted;
        ApplyMicMuted(muted);
    }

    private void ApplyMicMuted(bool muted)
    {
        if (_recorder == null) return;
        try
        {
            var builder = _recorder.GetDynamicOptionsBuilder();
            builder.SetDynamicAudioOptions(new DynamicAudioOptions { IsInputDeviceEnabled = true, InputVolume = muted ? 0f : 1f });
            builder.Apply();
        }
        catch (Exception ex)
        {
            Diagnostics.Log("SetMicMuted failed", ex);
        }
    }


    public void UpdateRegion(int x, int y, int width, int height)
    {
        if (_recorder == null || _sources.Count == 0) return;
        _activeRegion = new Rectangle(x, y, width, height);
        if (_paused)
        {
            _pendingRegionWhilePaused = true;
            return;
        }
        ApplyCurrentRegion();
    }

    private void ApplyCurrentRegion()
    {
        if (_recorder == null || _sources.Count == 0) return;
        try
        {
            var builder = _recorder.GetDynamicOptionsBuilder();
            builder.SetDynamicOutputOptions(new DynamicOutputOptions
            {
                SourceRect = GetCanvasCrop(_activeRegion),
            });
            for (int i = 0; i < _sources.Count; i++)
            {
                _sources[i].IsVideoCaptureEnabled = Intersects(_activeRegion, _sourceMonitors[i]);
                builder.SetUpdatedRecordingSource(_sources[i]);
            }
            builder.Apply();
        }
        catch (Exception ex)
        {
            Diagnostics.Log("UpdateRegion dynamic apply failed", ex);
        }
    }

    private ScreenRect GetCanvasCrop(Rectangle region)
    {
        var clipped = Rectangle.Intersect(region, _sourceCanvasBounds);
        if (clipped.Width <= 0 || clipped.Height <= 0)
            return new ScreenRect(0, 0, 1, 1);
        return new ScreenRect(clipped.X - _sourceCanvasBounds.X, clipped.Y - _sourceCanvasBounds.Y,
            clipped.Width, clipped.Height);
    }

    private static bool Intersects(Rectangle a, Rectangle b)
    {
        var r = Rectangle.Intersect(a, b);
        return r.Width > 0 && r.Height > 0;
    }

    private void OnComplete(object? sender, RecordingCompleteEventArgs e)
    {
        DisposeAppAudio(); // closes the raw audio file before it is muxed
        RecordingComplete?.Invoke(e.FilePath);
    }

    private void OnFailed(object? sender, RecordingFailedEventArgs e)
    {
        DisposeAppAudio();
        RecordingFailed?.Invoke(e.Error);
    }

    public void Dispose()
    {
        if (_recorder != null)
        {
            _recorder.OnRecordingComplete -= OnComplete;
            _recorder.OnRecordingFailed -= OnFailed;
            _recorder.OnStatusChanged -= OnStatusChanged;
            _recorder.Dispose();
            _recorder = null;
            _sources.Clear();
            _sourceMonitors.Clear();
        }
        DisposeAppAudio();
    }

    private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFOEX mi);

    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType;
        public int dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
