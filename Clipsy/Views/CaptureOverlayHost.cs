using System;
using Clipsy.Services;

namespace Clipsy.Views;

/// <summary>
/// Entry point for opening the capture overlay. Captures the screen first
/// so the overlay opens against a static (frozen) snapshot.
/// </summary>
public static class CaptureOverlayHost
{
    private static CaptureOverlayWindow? _current;
    private static readonly ScreenFreezeService _freeze = new();

    public static void ShowOverlay()
    {
        if (_current != null)
        {
            try { _current.Activate(); } catch { /* ignore */ }
            return;
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
