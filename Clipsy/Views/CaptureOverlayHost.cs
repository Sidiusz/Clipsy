using System;
using Clipsy.Services;

namespace Clipsy.Views;

/// <summary>Opens the capture overlay, capturing the screen first so it opens
/// against a static (frozen) snapshot.</summary>
public static class CaptureOverlayHost
{
    private static CaptureOverlayWindow? _current;   // shown overlay (null = hidden)
    private static CaptureOverlayWindow? _instance;  // persistent, reused across captures
    private static readonly ScreenFreezeService _freeze = new();

    public static void ShowOverlay()
    {
        if (_current != null)
        {
            // Re-activate the open overlay; if it's a dead handle, drop it and
            // fall through to build a fresh one instead of no-op'ing forever.
            try { _current.Activate(); return; }
            catch { _current = null; }
        }

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var frame = _freeze.Capture();
            long capMs = sw.ElapsedMilliseconds;

            if (_instance != null)
            {
                try
                {
                    _instance.PrepareForReuse(frame);
                    _current = _instance;
                    _current.Activate();
                    Diagnostics.Log($"Overlay open (reuse): capture={capMs}ms total={sw.ElapsedMilliseconds}ms");
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
            long ctorMs = sw.ElapsedMilliseconds - capMs;
            win.Closed += (_, _) =>
            {
                if (_current == win) _current = null;
                if (_instance == win) _instance = null;
            };
            _instance = win;
            _current = win;
            win.Activate();
            Diagnostics.Log($"Overlay open: capture={capMs}ms ctor={ctorMs}ms total={sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Overlay show failed: {ex}");
            Diagnostics.Log("CaptureOverlayHost.ShowOverlay", ex);
            _current = null;
        }
    }

    // Hides the overlay but keeps the instance warm for the next capture.
    internal static void Dismiss(CaptureOverlayWindow win)
    {
        if (_current == win) _current = null;
        try { win.HideAndReset(); }
        catch (Exception ex)
        {
            Diagnostics.Log("CaptureOverlayHost.Dismiss", ex);
            if (_instance == win) _instance = null;
            try { win.Close(); } catch { }
        }
    }
}
