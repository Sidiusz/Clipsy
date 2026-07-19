using System;
using Clipsy.Services;

namespace Clipsy.Views;

/// <summary>Opens the capture overlay, capturing the screen first so it opens
/// against a static (frozen) snapshot.</summary>
public static class CaptureOverlayHost
{
    private static CaptureOverlayWindow? _current;
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
            var frame = _freeze.Capture();
            var win = new CaptureOverlayWindow(frame);
            win.Closed += (_, _) => _current = null;
            _current = win;
            win.Activate();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Overlay show failed: {ex}");
            Diagnostics.Log("CaptureOverlayHost.ShowOverlay", ex);
            _current = null;
        }
    }
}
