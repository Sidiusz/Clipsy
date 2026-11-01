using System;
using System.Threading.Tasks;
using Clipsy.Drawing;
using Clipsy.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Rect = Windows.Foundation.Rect;

namespace Clipsy.Views;

/// <summary>Window picking through the "Select app" menu.
/// Recording that selection records the window itself, with its own sound.</summary>
public sealed partial class CaptureOverlayWindow
{
    private WindowTargets.Target? _selectedWindow;

    private Task _windowPaint = Task.CompletedTask;

    // The frozen screen shows whatever covered the window; swap in the window's own pixels so the
    // overlay, Copy and Save all show the picked app.
    private Task ShowPickedWindowAsync(WindowTargets.Target w)
    {
        int session = _session;
        var frame = _frame;
        return _windowPaint = Task.Run(() => CaptureWindowPixels(w) is { } image
            ? ScreenFreezeService.WithImageAt(frame, image, w.Bounds)
            : null).ContinueWith(t =>
        {
            if (session != _session || !ReferenceEquals(frame, _frame)) return;
            if (t.Result is not { } patched)
            {
                NotificationService.Info("InfoWindowCaptureFailed");
                return;
            }
            _frame = patched;
            _eyedropperPixels = null;
            TryLoadFrozenImage();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static ScreenshotRenderer.RenderedImage? CaptureWindowPixels(WindowTargets.Target w)
    {
        try { return ModernScreenCaptureService.CaptureWindow(w.Hwnd); }
        catch (Exception ex) { Diagnostics.Log("Picked window: no Graphics Capture frame, printing the window", ex); }
        try { return WindowPrintCapture.Capture(w.Hwnd, w.Bounds); }
        catch (Exception ex)
        {
            Diagnostics.Log("Picked window: printing failed", ex);
            return null;
        }
    }

    // Copy/Save right after picking must not render the frame from before the window was swapped in.
    private Task WaitForWindowPaintAsync() => _windowPaint.IsCompleted ? Task.CompletedTask : _windowPaint;

    /// <summary>Selects a window from the context menu, even one hidden behind others.</summary>
    private void SelectWindow(WindowTargets.Target w)
    {
        SetSelection(ClampToRoot(ToRoot(w.Bounds)));
        _selectedWindow = w;
        ShowSelectedWindowBadge();
        _ = ShowPickedWindowAsync(w);
    }

    // Moving, resizing or replacing the selection turns it back into a plain region.
    private void ClearSelectedWindow()
    {
        _selectedWindow = null;
        WindowPickBadge.Visibility = Visibility.Collapsed;
    }

    private void ShowSelectedWindowBadge()
    {
        if (_selectedWindow is not { } w) return;
        WindowPickTitle.Text = w.Title;
        WindowPickBadge.Visibility = Visibility.Visible;
        Canvas.SetLeft(WindowPickBadge, Math.Max(4, _selectionRect.X + 8));
        Canvas.SetTop(WindowPickBadge, Math.Max(4, _selectionRect.Y + 8));
    }

    private void ResetWindowPick() => ClearSelectedWindow();

    // Physical virtual-screen pixels → overlay coordinates, by the grid's own ratio like monitor snapping.
    private Rect ToRoot(System.Drawing.Rectangle r)
    {
        var b = _frame.VirtualBounds;
        double sx = RootGrid.ActualWidth / b.Width, sy = RootGrid.ActualHeight / b.Height;
        return new Rect((r.X - b.X) * sx, (r.Y - b.Y) * sy, r.Width * sx, r.Height * sy);
    }
}
