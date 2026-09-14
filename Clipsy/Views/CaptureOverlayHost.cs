using System;
using System.Diagnostics;
using System.Threading;
using Clipsy.Services;
using Microsoft.UI.Dispatching;

namespace Clipsy.Views;

public static class CaptureOverlayHost
{
    private static CaptureOverlayWindow? _current;
    private static CaptureOverlayWindow? _instance;
    private static readonly ScreenFreezeService _freeze = new();
    private static readonly AutoResetEvent _captureSignal = new(false);
    private static DispatcherQueue? _dispatcher;
    private static Thread? _captureThread;
    private static volatile bool _running;
    private static int _requestInFlight;
    private static int _overlayVisible;
    private static long _requestTick;

    public static void Initialize(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        if (_captureThread != null) return;
        _running = true;
        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "Clipsy.Capture",
            Priority = ThreadPriority.AboveNormal,
        };
        _captureThread.Start();
    }

    public static void RequestOverlay()
    {
        var dq = _dispatcher;
        if (dq == null) return;

        if (Volatile.Read(ref _overlayVisible) != 0)
        {
            dq.TryEnqueue(() =>
            {
                try { _current?.Activate(); }
                catch
                {
                    _current = null;
                    Volatile.Write(ref _overlayVisible, 0);
                    RequestOverlay();
                }
            });
            return;
        }

        Interlocked.Exchange(ref _requestTick, Environment.TickCount64);
        if (Interlocked.CompareExchange(ref _requestInFlight, 1, 0) == 0)
            _captureSignal.Set();
    }

    public static void ShowOverlay() => RequestOverlay();

    private static void CaptureLoop()
    {
        while (_running)
        {
            _captureSignal.WaitOne();
            if (!_running) break;
            try
            {
                long requestTick = Interlocked.Read(ref _requestTick);
                long workerWaitMs = Math.Max(0, Environment.TickCount64 - requestTick);
                var sw = Stopwatch.StartNew();
                var frame = _freeze.Capture();
                long captureMs = sw.ElapsedMilliseconds;
                var dq = _dispatcher;
                if (dq == null || !dq.TryEnqueue(() => ShowCapturedFrame(frame, requestTick, workerWaitMs, captureMs)))
                    Interlocked.Exchange(ref _requestInFlight, 0);
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _requestInFlight, 0);
                Diagnostics.Log("Capture worker failed", ex);
            }
        }
    }

    private static void ShowCapturedFrame(ScreenFreezeService.FrozenFrame frame,
        long requestTick, long workerWaitMs, long captureMs)
    {
        try
        {
            if (_current != null)
            {
                _current.Activate();
                Volatile.Write(ref _overlayVisible, 1);
                return;
            }

            var sw = Stopwatch.StartNew();
            if (_instance != null)
            {
                try
                {
                    _instance.PrepareForReuse(frame);
                    _current = _instance;
                    _current.Activate();
                    Volatile.Write(ref _overlayVisible, 1);
                    LogTiming("reuse", frame, requestTick, workerWaitMs, captureMs, sw.ElapsedMilliseconds);
                    return;
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("CaptureOverlayHost reuse failed", ex);
                    try { _instance.Close(); } catch { }
                    _instance = null;
                }
            }

            var win = new CaptureOverlayWindow(frame);
            win.Closed += (_, _) =>
            {
                if (_current == win) _current = null;
                if (_instance == win) _instance = null;
                Volatile.Write(ref _overlayVisible, 0);
            };
            _instance = win;
            _current = win;
            win.Activate();
            Volatile.Write(ref _overlayVisible, 1);
            LogTiming("new", frame, requestTick, workerWaitMs, captureMs, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _current = null;
            Volatile.Write(ref _overlayVisible, 0);
            Diagnostics.Log("CaptureOverlayHost.ShowCapturedFrame", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _requestInFlight, 0);
        }
    }

    private static void LogTiming(string mode, ScreenFreezeService.FrozenFrame frame,
        long requestTick, long workerWaitMs, long captureMs, long uiMs)
    {
        long totalMs = Math.Max(0, Environment.TickCount64 - requestTick);
        var b = frame.VirtualBounds;
        Diagnostics.Log($"Overlay open ({mode}): total={totalMs}ms workerWait={workerWaitMs}ms capture={captureMs}ms ui={uiMs}ms " +
            $"frame={frame.PixelWidth}x{frame.PixelHeight} bounds={b.X},{b.Y},{b.Width}x{b.Height} monitors={frame.Monitors.Count}");
    }

    internal static void Dismiss(CaptureOverlayWindow win)
    {
        if (_current == win) _current = null;
        Volatile.Write(ref _overlayVisible, 0);
        try { win.HideAndReset(); }
        catch (Exception ex)
        {
            Diagnostics.Log("CaptureOverlayHost.Dismiss", ex);
            if (_instance == win) _instance = null;
            try { win.Close(); } catch { }
        }
    }

    public static void Shutdown()
    {
        _running = false;
        _captureSignal.Set();
        try { _captureThread?.Join(1000); } catch { }
        _captureThread = null;
        _dispatcher = null;
        Interlocked.Exchange(ref _requestInFlight, 0);
        Volatile.Write(ref _overlayVisible, 0);
    }
}
