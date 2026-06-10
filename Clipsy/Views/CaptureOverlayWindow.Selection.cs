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
        // Position via render transform, not Margin — Margin invalidates the
        // whole RootGrid layout on every pointer move, which is the main FPS
        // killer on 1440p+ screens.
        SelectionTranslate.X = _selectionRect.X;
        SelectionTranslate.Y = _selectionRect.Y;

        SelectionBorder.Width = _selectionRect.Width;
        SelectionBorder.Height = _selectionRect.Height;

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

    // ---------- Per-frame coalescing ----------

    // Pointer events arrive far more often than the compositor renders
    // (high-polling mice). Batch all selection-visual work to one pass per
    // CompositionTarget.Rendering tick.
    private bool _selectionVisualDirty;
    private bool _selectionRenderHooked;

    private void RequestSelectionVisualUpdate()
    {
        _selectionVisualDirty = true;
        if (_selectionRenderHooked) return;
        _selectionRenderHooked = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnSelectionRenderTick;
    }

    private void OnSelectionRenderTick(object? sender, object e)
    {
        if (_selectionVisualDirty)
        {
            _selectionVisualDirty = false;
            UpdateSelectionVisual();
            return; // stay hooked while updates keep coming
        }
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnSelectionRenderTick;
        _selectionRenderHooked = false;
    }

    // ---------- Toolbars ----------

    // Drag-end corner for "dynamic tool islands" (true = right/bottom).
    private bool _anchorRight = true;
    private bool _anchorBottom = true;

    // Toolbar sizes are stable while dragging; measuring on every pointer
    // move forces extra layout passes. Cached on ShowToolbars.
    private Size _bottomTbSize;
    private Size _rightTbSize;

    private void ShowToolbars()
    {
        BottomToolbar.Visibility = Visibility.Visible;
        RightToolbar.Visibility = Visibility.Visible;
        _bottomTbSize = default;
        _rightTbSize = default;
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
        if (_bottomTbSize.Width <= 0)
        {
            BottomToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _bottomTbSize = BottomToolbar.DesiredSize;
        }
        if (_rightTbSize.Width <= 0)
        {
            RightToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _rightTbSize = RightToolbar.DesiredSize;
        }
        var rootW = RootGrid.ActualWidth;
        var rootH = RootGrid.ActualHeight;

        double bw = _bottomTbSize.Width;
        double bh = _bottomTbSize.Height;
        double rw = _rightTbSize.Width;
        double rh = _rightTbSize.Height;

        double selX = _selectionRect.X;
        double selY = _selectionRect.Y;
        double selR = _selectionRect.X + _selectionRect.Width;
        double selB = _selectionRect.Y + _selectionRect.Height;

        double bx, by, rx, ry;

        // Placement per axis: preferred side outside → opposite side outside →
        // inside the selection. Never lets an island leave the screen.
        double PlaceOutsideOrInside(double prefer, double opposite, double inside, double size, double limit)
        {
            if (prefer >= 8 && prefer + size <= limit - 8) return prefer;
            if (opposite >= 8 && opposite + size <= limit - 8) return opposite;
            return inside;
        }

        if (Clipsy.Services.SettingsService.Instance.Settings.DynamicToolbarIslands)
        {
            // Both islands dock to the corner where the selection drag ended,
            // aligned edge-to-corner (not centered). When the anchored side
            // has no room, flip to the opposite side; inside is the last resort.
            bx = _anchorRight ? selR - bw : selX;
            by = PlaceOutsideOrInside(
                prefer:   _anchorBottom ? selB + 12 : selY - bh - 12,
                opposite: _anchorBottom ? selY - bh - 12 : selB + 12,
                inside:   _anchorBottom ? selB - bh - 8 : selY + 8,
                size: bh, limit: rootH);

            rx = PlaceOutsideOrInside(
                prefer:   _anchorRight ? selR + 12 : selX - rw - 12,
                opposite: _anchorRight ? selX - rw - 12 : selR + 12,
                inside:   _anchorRight ? selR - rw - 8 : selX + 8,
                size: rw, limit: rootW);
            ry = _anchorBottom ? selB - rh : selY;
        }
        else
        {
            bx = selX + (_selectionRect.Width - bw) / 2;
            by = PlaceOutsideOrInside(
                prefer:   selB + 12,
                opposite: selY - bh - 12,
                inside:   selB - bh - 8,
                size: bh, limit: rootH);

            ry = selY + (_selectionRect.Height - rh) / 2;
            rx = PlaceOutsideOrInside(
                prefer:   selR + 12,
                opposite: selX - rw - 12,
                inside:   selR - rw - 8,
                size: rw, limit: rootW);
        }

        bx = System.Math.Clamp(bx, 8, System.Math.Max(8, rootW - bw - 8));
        by = System.Math.Clamp(by, 8, System.Math.Max(8, rootH - bh - 8));
        rx = System.Math.Clamp(rx, 8, System.Math.Max(8, rootW - rw - 8));
        ry = System.Math.Clamp(ry, 8, System.Math.Max(8, rootH - rh - 8));

        // Islands must never overlap each other: slide the horizontal bar
        // sideways past the vertical island; if that can't fit, slide the
        // vertical island up/down instead.
        if (RectsIntersect(bx, by, bw, bh, rx, ry, rw, rh))
        {
            double leftCand = rx - bw - 8;
            double rightCand = rx + rw + 8;
            bool preferLeft = bx + bw / 2 <= rx + rw / 2;
            if (preferLeft && leftCand >= 8) bx = leftCand;
            else if (rightCand + bw <= rootW - 8) bx = rightCand;
            else if (leftCand >= 8) bx = leftCand;
            else
            {
                double upCand = by - rh - 8;
                double downCand = by + bh + 8;
                bool preferUp = ry + rh / 2 <= by + bh / 2;
                if (preferUp && upCand >= 8) ry = upCand;
                else if (downCand + rh <= rootH - 8) ry = downCand;
                else if (upCand >= 8) ry = upCand;
            }
        }

        Canvas.SetLeft(BottomToolbar, bx);
        Canvas.SetTop(BottomToolbar, by);
        Canvas.SetLeft(RightToolbar, rx);
        Canvas.SetTop(RightToolbar, ry);
    }

    private static bool RectsIntersect(double ax, double ay, double aw, double ah,
                                       double bx, double by, double bw, double bh)
        => ax < bx + bw && bx < ax + aw && ay < by + bh && by < ay + ah;

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
