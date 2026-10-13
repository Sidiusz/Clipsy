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
    private static int _pendingCancelled;
    private static long _escapeProbeTick;
    private static long _lastEscapeTick;
    private static volatile bool _shuttingDown;

    // A second Esc this long after an unanswered first one ends the process to release the
    // screen. A single Esc never does: the UI thread may just be busy for a few seconds.
    private const int HungEscapeConfirmMs = 3000;
    // Esc events closer together than this are the key's auto-repeat, not a new press.
    private const int EscapeRepeatGapMs = 200;
    // HIGH priority covers the opening and the first input, then a game under the open
    // editor (OCR, encoding, the user thinking) gets its share back.
    private const int BoostAfterRevealMs = 3000;

    /// <summary>The app is exiting: the overlay may really close instead of hiding for reuse.</summary>
    internal static bool IsShuttingDown => _shuttingDown;

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
                // Held until shortly after the reveal (or the close); under game load the capture,
                // the first frames and the first input all wait on CPU/GPU time otherwise.
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
                    _current.Activate();
                    Volatile.Write(ref _overlayVisible, 1);
                    LogTiming("reuse", frame, requestTick, workerWaitMs, captureMs, queueMs, sw.ElapsedMilliseconds);
                    return;
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("CaptureOverlayHost reuse failed", ex);
                    try { _instance.ForceClose(); } catch { }
                    _instance = null;
                }
            }

            var win = new CaptureOverlayWindow(frame);
            win.Closed += (_, _) =>
            {
                if (_current == win)
                {
                    // Before the visible flag drops: a capture requested right after must boost again.
                    CapturePriority.Restore();
                    _current = null;
                    Volatile.Write(ref _overlayVisible, 0);
                }
                if (_instance == win) _instance = null;
            };
            _instance = win;
            _current = win;
            win.Activate();
            Volatile.Write(ref _overlayVisible, 1);
            LogTiming("new", frame, requestTick, workerWaitMs, captureMs, queueMs, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _current = null;
            CapturePriority.Restore();
            Volatile.Write(ref _overlayVisible, 0);
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

    /// <summary>The overlay started closing (already cloaked): a capture requested from now
    /// on opens a fresh session instead of re-activating the closing one.</summary>
    internal static void Detach(CaptureOverlayWindow win)
    {
        // Only the current overlay: a late call must not drop the boost of a capture in flight.
        if (_current != win) return;
        _current = null;
        // Before the visible flag drops: a capture requested right after must boost again.
        CapturePriority.Restore();
        Volatile.Write(ref _overlayVisible, 0);
    }

    /// <summary>The overlay is on screen: keep the boost a moment for the first input, then drop it.</summary>
    internal static void OnRevealed(CaptureOverlayWindow win)
    {
        if (_current == win) CapturePriority.RestoreAfter(BoostAfterRevealMs);
    }

    internal static void Dismiss(CaptureOverlayWindow win)
    {
        Detach(win);
        try { win.HideAndReset(); }
        catch (Exception ex)
        {
            Diagnostics.Log("CaptureOverlayHost.Dismiss", ex);
            if (_instance == win) _instance = null;
            try { win.ForceClose(); } catch { }
        }
    }

    public static void Shutdown()
    {
        _shuttingDown = true;
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
    internal static void OnGlobalEscape(bool withModifiers)
    {
        if (Volatile.Read(ref _overlayVisible) == 0)
        {
            if (Volatile.Read(ref _requestInFlight) == 0) return;
            Volatile.Write(ref _pendingCancelled, 1);
            // The overlay may have been shown between the two reads; then it handles this Esc.
            if (Volatile.Read(ref _overlayVisible) == 0) return;
            Volatile.Write(ref _pendingCancelled, 0);
        }
        var dq = _dispatcher;
        var overlay = _current;
        if (dq == null || overlay == null) return;

        IntPtr fg = GetForegroundWindow();
        if (fg != overlay.Hwnd && !withModifiers && IsCoveredBy(fg, overlay.Hwnd))
        {
            // The Esc went to an app that took the focus while our overlay still hides it
            // from the user. Windows above the overlay (Start, Alt+Tab, Task Manager, our
            // own dialogs) are visible and get their Esc as usual.
            dq.TryEnqueue(DispatcherQueuePriority.High, () =>
            {
                if (_current == overlay) overlay.CancelFromOutside();
            });
        }

        // Probe the UI thread with plain Esc only: Ctrl+Shift+Esc opens Task Manager.
        if (withModifiers) return;
        long now = Environment.TickCount64;
        bool repeat = now - Interlocked.Exchange(ref _lastEscapeTick, now) < EscapeRepeatGapMs;
        long probe = Interlocked.Read(ref _escapeProbeTick);
        if (probe == 0)
        {
            if (Interlocked.CompareExchange(ref _escapeProbeTick, now, 0) != 0) return;
            if (!dq.TryEnqueue(DispatcherQueuePriority.High, () => Interlocked.Exchange(ref _escapeProbeTick, 0)))
                Interlocked.CompareExchange(ref _escapeProbeTick, 0, now);
            return;
        }
        // A second, separate Esc while the first is still unanswered: the overlay is wedged on screen.
        if (!repeat && now - probe >= HungEscapeConfirmMs) ReleaseWedgedOverlay(probe);
    }

    // True when `window` is a real window below the overlay in z-order, i.e. the user can't see it.
    private static bool IsCoveredBy(IntPtr window, IntPtr overlay)
    {
        if (window == IntPtr.Zero || overlay == IntPtr.Zero) return false;
        IntPtr w = window;
        for (int i = 0; i < 10000; i++)
        {
            w = GetWindow(w, GW_HWNDPREV);
            if (w == IntPtr.Zero) return false;
            if (w == overlay) return true;
        }
        return false;
    }

    private static void ReleaseWedgedOverlay(long probe)
    {
        // Only when the overlay is actually on screen (a cloaked one blocks nothing), and
        // never while a recording is running or still being finalized.
        if (Volatile.Read(ref _overlayVisible) == 0 || _current is not { IsRevealed: true }
            || RecordingController.Current != null)
        {
            Interlocked.CompareExchange(ref _escapeProbeTick, 0, probe);
            return;
        }
        // Off the hook thread: the hook callback must return immediately.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (Interlocked.Read(ref _escapeProbeTick) != probe) return; // the UI thread answered
            // The UI thread is the only one that can hide the window, and it isn't running.
            // End the process so the screen is usable again; the watchdog starts Clipsy anew.
            Diagnostics.Log($"Overlay UI thread unresponsive for {Environment.TickCount64 - probe} ms after a repeated Esc; restarting Clipsy to release the screen");
            try { ProcessWatchdog.RequestRestart(); }
            catch (Exception ex) { Diagnostics.Log("Watchdog restart request failed", ex); }
            try { Process.GetCurrentProcess().Kill(); }
            catch (Exception ex) { Diagnostics.Log("Emergency overlay release failed", ex); }
        });
    }

    private const uint GW_HWNDPREV = 3;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
