using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
    private double _outputAspect;
    private string[]? _fpsDevices; // displays whose refresh rate "native" fps follows
    private string _tempPath = string.Empty;

    public event Action<string>? RecordingComplete;
    public event Action<string>? RecordingFailed;

    public string TempPath => _tempPath;

    public static string TempDirectory => Path.Combine(Path.GetTempPath(), "Clipsy");

    /// <summary>Screen recordings capture through Windows Graphics Capture; Desktop Duplication
    /// misses updates from some apps (flip-model windows) and only re-sends a frame every 500 ms.</summary>
    public bool UsesGraphicsCapture { get; private set; }

    public void Start(int x, int y, int width, int height, string? overrideCodec = null, bool desktopDuplication = false)
    {
        PrepareTempPath();
        bool cursor = SettingsService.Instance.Settings.CaptureVideoCursor;
        UsesGraphicsCapture = !desktopDuplication && ModernScreenCaptureService.IsSupported;
        BuildSources(x, y, width, height, cursor);
        _fpsDevices = DevicesTouching(new Rectangle(x, y, width, height));
        _activeRegion = new Rectangle(x, y, width, height);
        var (outW, outH) = ResolveOutputSize(SettingsService.Instance.Settings.VideoResolution, width, height);
        _outputAspect = (double)outW / outH;
        var crop = GetCanvasCrop(_activeRegion);
        Diagnostics.Log($"Recording region={_activeRegion} canvas={_sourceCanvasBounds} crop={crop.Left},{crop.Top},{crop.Width},{crop.Height} out={outW}x{outH} fps={ResolveFramerate(SettingsService.Instance.Settings.VideoFramerate, _fpsDevices)} api={(UsesGraphicsCapture ? "WGC" : "DDA")} monitors={string.Join(";", _sourceMonitors)}");
        StartRecorder(_sources.Cast<RecordingSourceBase>().ToList(), width, height, crop,
            overrideCodec, systemAudio: true, cursor);
    }

    /// <summary>Records one window wherever it moves. With <paramref name="audioProcessId"/> the system mix is
    /// replaced by that process tree's audio, written to <see cref="AppAudioPath"/> for muxing on save.</summary>
    public void StartWindow(IntPtr hwnd, int width, int height, int? audioProcessId, string? overrideCodec = null)
    {
        PrepareTempPath();
        bool cursor = SettingsService.Instance.Settings.CaptureVideoCursor;
        var wr = WindowTargets.Bounds(hwnd);
        _fpsDevices = DevicesTouching(new Rectangle(wr.X, wr.Y, Math.Max(1, wr.Width), Math.Max(1, wr.Height)));
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
                Framerate = ResolveFramerate(s.VideoFramerate, _fpsDevices),
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
                RecorderApi = UsesGraphicsCapture ? RecorderApi.WindowsGraphicsCapture : RecorderApi.DesktopDuplication,
                IsBorderRequired = false,
            };
            _sources.Add(source);
            _sourceMonitors.Add(bounds);
        }
        if (_sources.Count == 0)
            throw new InvalidOperationException("No display recording sources are available.");
    }

    internal static string[] DevicesTouching(Rectangle region)
        => EnumerateMonitors().Where(m => Intersects(region, m.Bounds)).Select(m => m.DeviceName).ToArray();

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

    /// <summary>Maps the VideoFramerate setting to fps; 0 = native: the highest refresh rate of
    /// <paramref name="devices"/> (the displays being recorded), or the primary display's.
    /// A slower display just repeats frames, which the variable-rate encoder skips.</summary>
    public static int ResolveFramerate(int setting, IReadOnlyList<string>? devices = null)
    {
        if (setting > 0) return Math.Clamp(setting, 10, 240);
        int hz = 0;
        foreach (var device in devices is { Count: > 0 } ? devices : new string?[] { null })
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmDisplayFrequency > 1)
                hz = Math.Max(hz, dm.dmDisplayFrequency);
        }
        return hz > 0 ? Math.Clamp(hz, 10, 240) : 60;
    }

    internal static (int w, int h) ResolveOutputSize(string resolution, int captureW, int captureH)
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

    // Serializes recorder calls: region updates run on a worker while the UI thread pauses, mutes and stops.
    private readonly object _recorderLock = new();
    private readonly object _regionLock = new();
    private bool _regionDirty;
    private bool _regionWorkerRunning;

    public void Pause() { lock (_recorderLock) { _paused = true; _recorder?.Pause(); } }
    public void Resume()
    {
        lock (_recorderLock)
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
            ApplyCurrentRegion(CurrentRegion());
        }
        }
    }
    public void Stop() { lock (_recorderLock) _recorder?.Stop(); }

    public void SetMicMuted(bool muted)
    {
        _micMuted = muted;
        ApplyMicMuted(muted);
    }

    private void ApplyMicMuted(bool muted)
    {
        lock (_recorderLock)
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
    }


    public void UpdateRegion(int x, int y, int width, int height)
    {
        lock (_regionLock)
        {
            _activeRegion = new Rectangle(x, y, width, height);
            _regionDirty = true;
            if (_regionWorkerRunning) return;
            _regionWorkerRunning = true;
        }
        Task.Run(ApplyQueuedRegions);
    }

    private Rectangle CurrentRegion()
    {
        lock (_regionLock) return _activeRegion;
    }

    // Apply() blocks for a while, so drags go through here: only the newest region is applied.
    private void ApplyQueuedRegions()
    {
        while (true)
        {
            Rectangle region;
            lock (_regionLock)
            {
                if (!_regionDirty)
                {
                    _regionWorkerRunning = false;
                    return;
                }
                _regionDirty = false;
                region = _activeRegion;
            }
            lock (_recorderLock)
            {
                if (_paused) _pendingRegionWhilePaused = true;
                else ApplyCurrentRegion(region);
            }
        }
    }

    private void ApplyCurrentRegion(Rectangle region)
    {
        if (_recorder == null || _sources.Count == 0) return;
        try
        {
            var builder = _recorder.GetDynamicOptionsBuilder();
            builder.SetDynamicOutputOptions(new DynamicOutputOptions
            {
                SourceRect = GetCanvasCrop(region),
            });
            for (int i = 0; i < _sources.Count; i++)
            {
                _sources[i].IsVideoCaptureEnabled = Intersects(region, _sourceMonitors[i]);
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
        clipped = FitAspect(clipped, _outputAspect);
        return new ScreenRect(clipped.X - _sourceCanvasBounds.X, clipped.Y - _sourceCanvasBounds.Y,
            clipped.Width, clipped.Height);
    }

    // The video frame is fixed at start; letterboxing a differently shaped crop would fill the bars
    // with the screen around the region, so the crop is trimmed to the frame's shape instead.
    internal static Rectangle FitAspect(Rectangle r, double aspect)
    {
        if (aspect <= 0 || r.Width <= 0 || r.Height <= 0) return r;
        double current = (double)r.Width / r.Height;
        if (current > aspect)
        {
            int w = Math.Clamp((int)Math.Round(r.Height * aspect), 1, r.Width);
            return new Rectangle(r.X + (r.Width - w) / 2, r.Y, w, r.Height);
        }
        int h = Math.Clamp((int)Math.Round(r.Width / aspect), 1, r.Height);
        return new Rectangle(r.X, r.Y + (r.Height - h) / 2, r.Width, h);
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
        lock (_recorderLock)
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
