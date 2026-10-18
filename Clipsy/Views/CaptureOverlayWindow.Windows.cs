using System;
using Clipsy.Drawing;
using Clipsy.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;

namespace Clipsy.Views;

/// <summary>Window picking: before anything is selected the window under the cursor is highlighted,
/// and a click selects it. Recording that selection records the window itself, with its own sound.</summary>
public sealed partial class CaptureOverlayWindow
{
    private WindowTargets.Target? _hoverWindow;
    private WindowTargets.Target? _selectedWindow;

    private bool CanPickWindow =>
        !_hasSelection && _mode == InteractionMode.Idle && !_inOcrMode && !_eyedropperActive
        && _drawing.Settings.Tool == ToolKind.None;

    private void UpdateWindowHover(Point pos)
    {
        var hit = CanPickWindow ? WindowAt(pos) : null;
        if (ReferenceEquals(hit, _hoverWindow)) return;
        _hoverWindow = hit;
        if (hit == null)
        {
            HideWindowPick();
            UpdateDimGeometry(null);
            return;
        }
        var r = ToRoot(hit.Bounds);
        ShowWindowPick(r, hit.Title, outline: true);
        UpdateDimGeometry(r);
    }

    /// <summary>A click without a drag: select the window under it. False when there is none.</summary>
    private bool TrySelectWindowAt(Point pos)
    {
        var hit = WindowAt(pos);
        if (hit == null) return false;
        HideWindowPick();
        _hoverWindow = null;
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
        if (_selectedWindow is { } w) ShowWindowPick(_selectionRect, w.Title, outline: false);
    }

    private void ResetWindowPick()
    {
        _hoverWindow = null;
        _selectedWindow = null;
        HideWindowPick();
    }

    private void ShowWindowPick(Rect r, string title, bool outline)
    {
        WindowPickRect.Visibility = outline ? Visibility.Visible : Visibility.Collapsed;
        if (outline)
        {
            Canvas.SetLeft(WindowPickRect, r.X);
            Canvas.SetTop(WindowPickRect, r.Y);
            WindowPickRect.Width = Math.Max(0, r.Width);
            WindowPickRect.Height = Math.Max(0, r.Height);
        }
        WindowPickTitle.Text = title;
        WindowPickBadge.Visibility = Visibility.Visible;
        Canvas.SetLeft(WindowPickBadge, Math.Max(4, r.X + 8));
        Canvas.SetTop(WindowPickBadge, Math.Max(4, r.Y + 8));
    }

    private void HideWindowPick()
    {
        WindowPickRect.Visibility = Visibility.Collapsed;
        WindowPickBadge.Visibility = Visibility.Collapsed;
    }

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
