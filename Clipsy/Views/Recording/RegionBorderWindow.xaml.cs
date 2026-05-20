using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Clipsy.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace Clipsy.Views.Recording;

public sealed partial class RegionBorderWindow : Window
{
    private const int GrabMarginPixels = 10;
    private const int MinRegionSize = 48;
    private const double HandleSize = 10.0;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly List<Rectangle> _handleVisuals = new();

    private bool _interactive;
    private bool _dragging;
    private DragMode _dragMode = DragMode.None;
    private Point _dragStartScreen;
    private int _startX, _startY, _startW, _startH;
    private int _curX, _curY, _curW, _curH;

    public event Action<int, int, int, int>? RegionChanged;

    public IntPtr Hwnd => _hwnd;

    public RegionBorderWindow()
    {
        InitializeComponent();
        ThemeService.Register(Content as FrameworkElement);
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        if (_appWindow.Presenter is OverlappedPresenter op)
        {
            op.SetBorderAndTitleBar(false, false);
            op.IsResizable = false;
            op.IsMaximizable = false;
            op.IsMinimizable = false;
            op.IsAlwaysOnTop = true;
        }
        _appWindow.IsShownInSwitchers = false;
        ApplyTransparencyClickThrough();
        DisableDwmDecorations();
        BuildHandles();
        UpdateVisuals();
    }

    private void DisableDwmDecorations()
    {
        try
        {
            int donotround = 1;
            DwmSetWindowAttribute(_hwnd, 33, ref donotround, sizeof(int));
            int ncDisabled = 1;
            DwmSetWindowAttribute(_hwnd, 2, ref ncDisabled, sizeof(int));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] DWM disable decorations failed: {ex.Message}");
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int pvAttribute, int cbAttribute);

    public void SetInteractive(bool interactive)
    {
        _interactive = interactive;
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = interactive ? ex & ~WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        UpdateVisuals();
    }

    public void MoveTo(int x, int y, int w, int h)
    {
        _curX = x;
        _curY = y;
        _curW = w;
        _curH = h;
        _appWindow.MoveAndResize(new RectInt32(x, y, w, h));
        UpdateVisuals();
    }

    private void ApplyTransparencyClickThrough()
    {
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
    }

    private void BuildHandles()
    {
        if (_handleVisuals.Count > 0) return;

        for (int i = 0; i < 8; i++)
        {
            var r = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1F, 0x6F, 0xEB)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            HandlesLayer.Children.Add(r);
            _handleVisuals.Add(r);
        }
    }

    private void UpdateVisuals()
    {
        SelectionBorder.Width = _curW;
        SelectionBorder.Height = _curH;

        HandlesLayer.Width = _curW;
        HandlesLayer.Height = _curH;

        if (!_interactive)
        {
            foreach (var hv in _handleVisuals) hv.Visibility = Visibility.Collapsed;
            return;
        }

        var anchors = GetClampedAnchors();
        for (int i = 0; i < _handleVisuals.Count; i++)
        {
            Canvas.SetLeft(_handleVisuals[i], anchors[i].X - HandleSize / 2);
            Canvas.SetTop(_handleVisuals[i], anchors[i].Y - HandleSize / 2);
            _handleVisuals[i].Visibility = Visibility.Visible;
        }
    }

    private (double X, double Y, DragMode Mode)[] GetClampedAnchors()
    {
        double w = _curW, h = _curH;
        var raw = new (double X, double Y, DragMode Mode)[]
        {
            (0, 0, DragMode.TopLeft), (w / 2, 0, DragMode.Top), (w, 0, DragMode.TopRight),
            (w, h / 2, DragMode.Right),
            (w, h, DragMode.BottomRight), (w / 2, h, DragMode.Bottom), (0, h, DragMode.BottomLeft),
            (0, h / 2, DragMode.Left),
        };

        double margin = HandleSize / 2 + 2;
        var result = new (double X, double Y, DragMode Mode)[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            double rx = raw[i].X;
            double ry = raw[i].Y;
            rx = Math.Clamp(rx, margin, Math.Max(margin, w - margin));
            ry = Math.Clamp(ry, margin, Math.Max(margin, h - margin));
            result[i] = (rx, ry, raw[i].Mode);
        }
        return result;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_interactive) return;

        _dragging = true;
        _dragMode = GetDragMode(e.GetCurrentPoint(SelectionCanvas).Position);
        if (GetCursorPos(out POINT pt))
        {
            _dragStartScreen = new Point(pt.X, pt.Y);
        }
        _startX = _curX;
        _startY = _curY;
        _startW = _curW;
        _startH = _curH;
        SelectionCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || !_interactive) return;
        if (!GetCursorPos(out POINT pt)) return;

        int dx = (int)(pt.X - _dragStartScreen.X);
        int dy = (int)(pt.Y - _dragStartScreen.Y);
        var (x, y, w, h) = ApplyDrag(dx, dy);
        if (x == _curX && y == _curY && w == _curW && h == _curH) return;
        MoveTo(x, y, w, h);
        RegionChanged?.Invoke(x, y, w, h);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        _dragMode = DragMode.None;
        SelectionCanvas.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private DragMode GetDragMode(Point local)
    {
        bool left = local.X <= GrabMarginPixels;
        bool right = local.X >= _curW - GrabMarginPixels;
        bool top = local.Y <= GrabMarginPixels;
        bool bottom = local.Y >= _curH - GrabMarginPixels;

        if (left && top) return DragMode.TopLeft;
        if (right && top) return DragMode.TopRight;
        if (left && bottom) return DragMode.BottomLeft;
        if (right && bottom) return DragMode.BottomRight;
        if (left) return DragMode.Left;
        if (right) return DragMode.Right;
        if (top) return DragMode.Top;
        if (bottom) return DragMode.Bottom;
        return DragMode.Move;
    }

    private (int X, int Y, int W, int H) ApplyDrag(int dx, int dy)
    {
        int x = _startX;
        int y = _startY;
        int w = _startW;
        int h = _startH;

        switch (_dragMode)
        {
            case DragMode.Move:
                x = _startX + dx;
                y = _startY + dy;
                break;
            case DragMode.Left:
                x = _startX + dx;
                w = _startW - dx;
                break;
            case DragMode.Right:
                w = _startW + dx;
                break;
            case DragMode.Top:
                y = _startY + dy;
                h = _startH - dy;
                break;
            case DragMode.Bottom:
                h = _startH + dy;
                break;
            case DragMode.TopLeft:
                x = _startX + dx;
                w = _startW - dx;
                y = _startY + dy;
                h = _startH - dy;
                break;
            case DragMode.TopRight:
                w = _startW + dx;
                y = _startY + dy;
                h = _startH - dy;
                break;
            case DragMode.BottomLeft:
                x = _startX + dx;
                w = _startW - dx;
                h = _startH + dy;
                break;
            case DragMode.BottomRight:
                w = _startW + dx;
                h = _startH + dy;
                break;
        }

        if (w < MinRegionSize)
        {
            if (_dragMode is DragMode.Left or DragMode.TopLeft or DragMode.BottomLeft)
                x = _startX + (_startW - MinRegionSize);
            w = MinRegionSize;
        }
        if (h < MinRegionSize)
        {
            if (_dragMode is DragMode.Top or DragMode.TopLeft or DragMode.TopRight)
                y = _startY + (_startH - MinRegionSize);
            h = MinRegionSize;
        }

        return (x, y, w, h);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    private enum DragMode { None, Move, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, int dwFlags);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
}
