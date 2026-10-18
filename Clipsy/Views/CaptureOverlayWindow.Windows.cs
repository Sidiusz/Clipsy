using System;
using Clipsy.Drawing;
using Clipsy.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;

namespace Clipsy.Views;

/// <summary>Window picking: a click (no drag) on a window selects it, as does the "Select app" menu.
/// Recording that selection records the window itself, with its own sound.</summary>
public sealed partial class CaptureOverlayWindow
{
    private WindowTargets.Target? _selectedWindow;

    /// <summary>A click without a drag: select the window under it. False when there is none.</summary>
    private bool TrySelectWindowAt(Point pos)
    {
        var hit = WindowAt(pos);
        if (hit == null) return false;
        _selectionRect = ClampToRoot(ToRoot(hit.Bounds));
        _selectedWindow = hit;
        return true;
    }

    /// <summary>Selects a window from the context menu, even one hidden behind others.</summary>
    private void SelectWindow(WindowTargets.Target w)
    {
        SetSelection(ClampToRoot(ToRoot(w.Bounds)));
        _selectedWindow = w;
        _selectionFromFallback = false;
        ShowSelectedWindowBadge();
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

    private WindowTargets.Target? WindowAt(Point pos)
    {
        var b = _frame.VirtualBounds;
        double gw = RootGrid.ActualWidth, gh = RootGrid.ActualHeight;
        if (gw <= 0 || gh <= 0 || b.Width <= 0 || b.Height <= 0) return null;
        int x = b.X + (int)(pos.X * b.Width / gw);
        int y = b.Y + (int)(pos.Y * b.Height / gh);
        foreach (var w in _frame.Windows)
            if (w.Bounds.Contains(x, y)) return w;
        return null;
    }

    // Physical virtual-screen pixels → overlay coordinates, by the grid's own ratio like monitor snapping.
    private Rect ToRoot(System.Drawing.Rectangle r)
    {
        var b = _frame.VirtualBounds;
        double sx = RootGrid.ActualWidth / b.Width, sy = RootGrid.ActualHeight / b.Height;
        return new Rect((r.X - b.X) * sx, (r.Y - b.Y) * sy, r.Width * sx, r.Height * sy);
    }
}
