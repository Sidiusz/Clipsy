using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;

namespace Clipsy.Views;

public sealed partial class CaptureOverlayWindow
{
    // ---------- Handles ----------

    private void BuildHandles()
    {
        for (int i = 0; i < 8; i++)
        {
            var r = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0x7D, 0x0D)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            HandlesLayer.Children.Add(r);
            _handleVisuals.Add(r);
        }
    }

    /// <summary>
    /// Anchor positions in selection-local coords, clamped so the handle stays
    /// fully visible when the selection touches a screen edge.
    /// </summary>
    private (double X, double Y, HandlePos H)[] GetClampedAnchors()
    {
        double w = _selectionRect.Width, h = _selectionRect.Height;
        var raw = new (double X, double Y, HandlePos H)[]
        {
            (0, 0, HandlePos.TL), (w / 2, 0, HandlePos.T), (w, 0, HandlePos.TR),
            (w, h / 2, HandlePos.R),
            (w, h, HandlePos.BR), (w / 2, h, HandlePos.B), (0, h, HandlePos.BL),
            (0, h / 2, HandlePos.L),
        };
        double rootW = RootGrid.ActualWidth;
        double rootH = RootGrid.ActualHeight;
        double margin = HandleSize / 2 + 2;
        var result = new (double X, double Y, HandlePos H)[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            double rx = _selectionRect.X + raw[i].X;
            double ry = _selectionRect.Y + raw[i].Y;
            rx = System.Math.Clamp(rx, margin, System.Math.Max(margin, rootW - margin));
            ry = System.Math.Clamp(ry, margin, System.Math.Max(margin, rootH - margin));
            result[i] = (rx - _selectionRect.X, ry - _selectionRect.Y, raw[i].H);
        }
        return result;
    }

    private void PositionHandles()
    {
        if (!_hasSelection)
        {
            foreach (var hv in _handleVisuals) hv.Visibility = Visibility.Collapsed;
            return;
        }
        var anchors = GetClampedAnchors();
        for (int i = 0; i < 8; i++)
        {
            Canvas.SetLeft(_handleVisuals[i], anchors[i].X - HandleSize / 2);
            Canvas.SetTop(_handleVisuals[i], anchors[i].Y - HandleSize / 2);
            _handleVisuals[i].Visibility = Visibility.Visible;
        }
    }

    private bool TryGetHandle(Point rootPos, out HandlePos handle)
    {
        handle = HandlePos.TL;
        if (!_hasSelection) return false;
        var local = new Point(rootPos.X - _selectionRect.X, rootPos.Y - _selectionRect.Y);
        double half = HandleSize / 2 + HandleHitInflate;
        foreach (var a in GetClampedAnchors())
        {
            if (System.Math.Abs(local.X - a.X) <= half && System.Math.Abs(local.Y - a.Y) <= half)
            {
                handle = a.H;
                return true;
            }
        }
        return false;
    }

    // ---------- Selection / drawing helpers ----------

    private bool IsInsideSelection(Point p)
    {
        return _hasSelection
            && p.X >= _selectionRect.X && p.X <= _selectionRect.X + _selectionRect.Width
            && p.Y >= _selectionRect.Y && p.Y <= _selectionRect.Y + _selectionRect.Height;
    }

    private Point ToCanvas(Point root) => new(root.X - _selectionRect.X, root.Y - _selectionRect.Y);

    private static Rect MakeRect(Point a, Point b)
    {
        double x = System.Math.Min(a.X, b.X);
        double y = System.Math.Min(a.Y, b.Y);
        double w = System.Math.Abs(a.X - b.X);
        double h = System.Math.Abs(a.Y - b.Y);
        return new Rect(x, y, w, h);
    }

    private Rect ResizeFromHandle(Rect baseRect, HandlePos h, Point pos)
    {
        double left = baseRect.X, top = baseRect.Y, right = baseRect.X + baseRect.Width, bottom = baseRect.Y + baseRect.Height;
        switch (h)
        {
            case HandlePos.TL: left = pos.X; top = pos.Y; break;
            case HandlePos.T:  top = pos.Y; break;
            case HandlePos.TR: right = pos.X; top = pos.Y; break;
            case HandlePos.R:  right = pos.X; break;
            case HandlePos.BR: right = pos.X; bottom = pos.Y; break;
            case HandlePos.B:  bottom = pos.Y; break;
            case HandlePos.BL: left = pos.X; bottom = pos.Y; break;
            case HandlePos.L:  left = pos.X; break;
        }
        if (right < left) (left, right) = (right, left);
        if (bottom < top) (top, bottom) = (bottom, top);
        return new Rect(left, top, right - left, bottom - top);
    }

    private void UpdateSelectionVisual()
    {
        if (_selectionRect.Width <= 0 || _selectionRect.Height <= 0)
        {
            SelectionLayer.Visibility = Visibility.Collapsed;
            UpdateDimGeometry(null);
            PositionToolbars();
            return;
        }

        SelectionLayer.Visibility = Visibility.Visible;
        SelectionLayer.Margin = new Thickness(_selectionRect.X, _selectionRect.Y, 0, 0);
        SelectionLayer.Width = _selectionRect.Width;
        SelectionLayer.Height = _selectionRect.Height;

        SelectionBorder.Width = _selectionRect.Width;
        SelectionBorder.Height = _selectionRect.Height;
        HandlesLayer.Width = _selectionRect.Width;
        HandlesLayer.Height = _selectionRect.Height;
        CursorPreviewLayer.Width = _selectionRect.Width;
        CursorPreviewLayer.Height = _selectionRect.Height;

        PositionHandles();
        UpdateDimGeometry(_selectionRect);
        PositionToolbars();
    }

    private void UpdateDimGeometry(Rect? hole)
    {
        double w = RootGrid.ActualWidth;
        double h = RootGrid.ActualHeight;
        if (w <= 0) w = _frame.VirtualBounds.Width;
        if (h <= 0) h = _frame.VirtualBounds.Height;

        _dimFull.Rect = new Rect(0, 0, w, h);

        // EvenOdd: a valid hole punches through the dim; an empty rect collapses
        // the second geometry so no hole shows (both rects identical → even fill = nothing).
        // Zero-size rect means no hole: EvenOdd ignores 0-area geometry.
        _dimHole.Rect = (hole.HasValue && hole.Value.Width > 0 && hole.Value.Height > 0)
            ? hole.Value
            : new Rect(0, 0, 0, 0);
    }

    private void ShowToolbars()
    {
        BottomToolbar.Visibility = Visibility.Visible;
        RightToolbar.Visibility = Visibility.Visible;
        PositionToolbars();
    }

    private void HideToolbars()
    {
        BottomToolbar.Visibility = Visibility.Collapsed;
        RightToolbar.Visibility = Visibility.Collapsed;
    }

    private void PositionToolbars()
    {
        if (BottomToolbar.Visibility != Visibility.Visible || !_hasSelection) return;
        BottomToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        RightToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var rootW = RootGrid.ActualWidth;
        var rootH = RootGrid.ActualHeight;

        double bw = BottomToolbar.DesiredSize.Width;
        double bh = BottomToolbar.DesiredSize.Height;
        double rw = RightToolbar.DesiredSize.Width;
        double rh = RightToolbar.DesiredSize.Height;

        double bx = _selectionRect.X + (_selectionRect.Width - bw) / 2;
        double by = _selectionRect.Y + _selectionRect.Height + 12;
        if (by + bh > rootH - 8)
        {
            by = _selectionRect.Y - bh - 12;
            if (by < 8) by = _selectionRect.Y + _selectionRect.Height - bh - 8;
        }
        bx = System.Math.Clamp(bx, 8, System.Math.Max(8, rootW - bw - 8));
        Canvas.SetLeft(BottomToolbar, bx);
        Canvas.SetTop(BottomToolbar, by);

        double rx = _selectionRect.X + _selectionRect.Width + 12;
        double ry = _selectionRect.Y + (_selectionRect.Height - rh) / 2;
        if (rx + rw > rootW - 8)
        {
            rx = _selectionRect.X - rw - 12;
            if (rx < 8) rx = _selectionRect.X + _selectionRect.Width - rw - 8;
        }
        ry = System.Math.Clamp(ry, 8, System.Math.Max(8, rootH - rh - 8));
        Canvas.SetLeft(RightToolbar, rx);
        Canvas.SetTop(RightToolbar, ry);
    }

    private void SelectAll()
    {
        var rect = new Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight);
        SetSelection(rect);
    }

    private void SetSelection(Rect rect)
    {
        _selectionRect = rect;
        _hasSelection = true;
        Hint.Visibility = Visibility.Collapsed;
        UpdateSelectionVisual();
        ShowToolbars();
    }
}
