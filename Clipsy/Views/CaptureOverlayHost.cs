using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private static IntPtr _currentHwnd;
    private static int _pendingCancelled;
    private static long _escapeProbeTick;
    private static Timer? _escapeProbeTimer;

    // How long the UI thread may leave an Esc unanswered before the overlay is torn down.
    private const int HungEscapeTimeoutMs = 3000;

    public static void Initialize(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        if (_captureThread != null) return;
        _running = true;
        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "Clipsy.Capture",
            Priority = ThreadPriority.Highest,
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
        // Pressing the hotkey again revives a request an Esc cancelled while it was pending.
        Volatile.Write(ref _pendingCancelled, 0);
        if (Interlocked.CompareExchange(ref _requestInFlight, 1, 0) == 0)
            _captureSignal.Set();
    }

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
                // Held until the overlay closes; under game load the capture, the first
                // frames and the input all wait on CPU/GPU time otherwise.
                CapturePriority.Boost();
                var sw = Stopwatch.StartNew();
                var frame = _freeze.Capture();
                long captureMs = sw.ElapsedMilliseconds;
                var dq = _dispatcher;
                long enqueuedTick = Environment.TickCount64;
                // High priority: jump ahead of queued toast/layout work so the overlay appears first.
                if (dq == null || !dq.TryEnqueue(DispatcherQueuePriority.High,
                        () => ShowCapturedFrame(frame, requestTick, workerWaitMs, captureMs, Environment.TickCount64 - enqueuedTick)))
                {
                    Interlocked.Exchange(ref _requestInFlight, 0);
                    CapturePriority.Restore();
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _requestInFlight, 0);
                if (Volatile.Read(ref _overlayVisible) == 0) CapturePriority.Restore();
                Diagnostics.Log("Capture worker failed", ex);
            }
        }
    }

    private static void ShowCapturedFrame(ScreenFreezeService.FrozenFrame frame,
        long requestTick, long workerWaitMs, long captureMs, long queueMs)
    {
        try
        {
            if (_current != null)
            {
                _current.Activate();
                Volatile.Write(ref _overlayVisible, 1);
                return;
            }

            // Esc pressed while the capture was still on its way: don't pop the overlay up late.
            if (Interlocked.Exchange(ref _pendingCancelled, 0) != 0)
            {
                CapturePriority.Restore();
                Diagnostics.LogInBackground($"Overlay open cancelled by Esc after {Math.Max(0, Environment.TickCount64 - requestTick)}ms");
                return;
            }

            var sw = Stopwatch.StartNew();
            if (_instance != null)
            {
                try
                {
                    _instance.PrepareForReuse(frame);
                    _current = _instance;
                    _currentHwnd = _current.Hwnd;
                    _current.Activate();
                    Volatile.Write(ref _overlayVisible, 1);
                    LogTiming("reuse", frame, requestTick, workerWaitMs, captureMs, queueMs, sw.ElapsedMilliseconds);
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
                if (_current == win) { _current = null; _currentHwnd = IntPtr.Zero; }
                if (_instance == win) _instance = null;
                Volatile.Write(ref _overlayVisible, 0);
                CapturePriority.Restore();
            };
            _instance = win;
            _current = win;
            _currentHwnd = win.Hwnd;
            win.Activate();
            Volatile.Write(ref _overlayVisible, 1);
            LogTiming("new", frame, requestTick, workerWaitMs, captureMs, queueMs, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _current = null;
            _currentHwnd = IntPtr.Zero;
            Volatile.Write(ref _overlayVisible, 0);
            CapturePriority.Restore();
            Diagnostics.Log("CaptureOverlayHost.ShowCapturedFrame", ex);
        }
        finally
        {
            Interlocked.Exchange(ref _requestInFlight, 0);
        }
    }

    private static void LogTiming(string mode, ScreenFreezeService.FrozenFrame frame,
        long requestTick, long workerWaitMs, long captureMs, long queueMs, long uiMs)
    {
        long totalMs = Math.Max(0, Environment.TickCount64 - requestTick);
        var b = frame.VirtualBounds;
        Diagnostics.LogInBackground($"Overlay open ({mode}): total={totalMs}ms workerWait={workerWaitMs}ms capture={captureMs}ms queue={queueMs}ms ui={uiMs}ms " +
            $"frame={frame.PixelWidth}x{frame.PixelHeight} bounds={b.X},{b.Y},{b.Width}x{b.Height} monitors={frame.Monitors.Count}");
    }

    internal static void Dismiss(CaptureOverlayWindow win)
    {
        if (_current == win) { _current = null; _currentHwnd = IntPtr.Zero; }
        Volatile.Write(ref _overlayVisible, 0);
        CapturePriority.Restore();
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
        CapturePriority.Restore();
    }

    // ---------- Esc from anywhere ----------

    /// <summary>Hook thread. Esc must free the screen even when the overlay can't take the
    /// key itself: another app grabbed the keyboard focus, or the UI thread is stalled
    /// under load while the full-screen topmost overlay blocks everything.</summary>
    internal static void OnGlobalEscape()
    {
        if (Volatile.Read(ref _overlayVisible) == 0)
        {
            if (Volatile.Read(ref _requestInFlight) != 0) Volatile.Write(ref _pendingCancelled, 1);
            return;
        }
        var dq = _dispatcher;
        if (dq == null) return;

        IntPtr fg = GetForegroundWindow();
        if (fg != _currentHwnd)
        {
            // Our own Save As dialog or menus handle their own Esc.
            GetWindowThreadProcessId(fg, out uint pid);
            if (pid == (uint)Environment.ProcessId) return;
            // Esc went to the app that took the focus, yet our overlay still covers the screen.
            if (!IsModifierDown())
                dq.TryEnqueue(DispatcherQueuePriority.High, () => _current?.CancelFromOutside());
        }

        // Probe the UI thread; if it can't answer, the overlay is wedged on screen.
        long now = Environment.TickCount64;
        if (Interlocked.CompareExchange(ref _escapeProbeTick, now, 0) != 0) return;
        dq.TryEnqueue(DispatcherQueuePriority.High, () => Interlocked.Exchange(ref _escapeProbeTick, 0));
        var timer = new Timer(CheckEscapeProbe, now, HungEscapeTimeoutMs, Timeout.Infinite);
        Interlocked.Exchange(ref _escapeProbeTimer, timer)?.Dispose();
    }

    private static void CheckEscapeProbe(object? state)
    {
        long probe = (long)state!;
        if (Interlocked.Read(ref _escapeProbeTick) != probe) return; // the UI thread answered
        if (Volatile.Read(ref _overlayVisible) == 0 || RecordingController.IsRecording)
        {
            Interlocked.CompareExchange(ref _escapeProbeTick, 0, probe);
            return;
        }
        // The UI thread is the only one that can hide the window, and it isn't running.
        // End the process so the screen is usable again; the watchdog starts Clipsy anew.
        Diagnostics.Log($"Overlay UI thread unresponsive for {HungEscapeTimeoutMs} ms after Esc; restarting Clipsy to release the screen");
        Diagnostics.Flush();
        try { Process.GetCurrentProcess().Kill(); }
        catch (Exception ex) { Diagnostics.Log("Emergency overlay release failed", ex); }
    }

    private static bool IsModifierDown() =>
        (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 || (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0
        || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
        || (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0 || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

    private const int VK_SHIFT   = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU    = 0x12;
    private const int VK_LWIN    = 0x5B;
    private const int VK_RWIN    = 0x5C;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
