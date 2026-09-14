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
    public event Action<RecorderStatus>? StatusChanged;

    public string TempPath => _tempPath;

    public void Start(int x, int y, int width, int height, string? overrideCodec = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Clipsy");
        Directory.CreateDirectory(tempDir);
        _tempPath = Path.Combine(tempDir, $"recording_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

        bool captureVideoCursor = SettingsService.Instance.Settings.CaptureVideoCursor;
        BuildSources(x, y, width, height, captureVideoCursor);
        _activeRegion = new Rectangle(x, y, width, height);

        var s = SettingsService.Instance.Settings;
        var (outW, outH) = ResolveOutputSize(s.VideoResolution, width, height);
        int bitrate = s.VideoBitrateMbps * 1_000_000;

        IVideoEncoder encoder = (overrideCodec ?? s.VideoCodec) switch
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
                RecordingSources = _sources.Cast<RecordingSourceBase>().ToList(),
            },
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                OutputFrameSize = new ScreenSize(outW, outH),
                SourceRect = GetCanvasCrop(_activeRegion),
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
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = true,
                IsInputDeviceEnabled = s.MicrophoneEnabled && !s.MicrophoneMuted,
                AudioInputDevice = s.MicrophoneEnabled && !string.IsNullOrEmpty(s.MicrophoneDevice)
                    ? s.MicrophoneDevice : null,
                IsOutputDeviceEnabled = true,
                Bitrate = AudioBitrate.bitrate_128kbps,
                Channels = AudioChannels.Stereo,
            },
            MouseOptions = new MouseOptions
            {
                IsMousePointerEnabled = captureVideoCursor,
                IsMouseClicksDetected = false,
            },
        };

        _recorder = Recorder.CreateRecorder(options);
        _recorder.OnRecordingComplete += OnComplete;
        _recorder.OnRecordingFailed += OnFailed;
        _recorder.OnStatusChanged += OnStatus;
        _recorder.Record(_tempPath);
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

    public void Pause() { _paused = true; _recorder?.Pause(); }
    public void Resume()
    {
        _paused = false;
        _recorder?.Resume();
        // Paused ScreenRecorderLib ignores Apply(); the new SourceRect never
        // reaches the encoder, so replay it on resume.
        if (_pendingRegionWhilePaused)
        {
            _pendingRegionWhilePaused = false;
            ApplyCurrentRegion();
        }
    }
    public void Stop() => _recorder?.Stop();

    public void SetMicMuted(bool muted)
    {
        if (_recorder == null) return;
        try
        {
            var builder = _recorder.GetDynamicOptionsBuilder();
            builder.SetDynamicAudioOptions(new DynamicAudioOptions { IsInputDeviceEnabled = !muted });
            builder.Apply();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] SetMicMuted failed: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"[Clipsy] UpdateRegion dynamic apply failed: {ex.Message}");
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
            _sources.Clear();
            _sourceMonitors.Clear();
        }
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
