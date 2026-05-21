using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using Clipsy.Services;

namespace Clipsy.Views.Recording;

/// <summary>
/// Transparent click-through overlay over the recording region. Magenta
/// background is used as the layered window color key, so the Canvas
/// appears fully transparent except where strokes are painted.
/// WS_EX_TRANSPARENT is toggled to switch between draw mode (the user can
/// click and paint) and pass-through mode (clicks fall through to the app
/// being recorded). The screen recorder picks up the rendered strokes
/// naturally.
/// </summary>
public sealed partial class RecordingDrawingWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED     = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    private const int LWA_COLORKEY = 0x00000001;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly List<Polyline> _strokes = new();
    private Polyline? _active;
    private bool _drawing;
    private bool _clickThrough = true;
    private Color _color = Microsoft.UI.Colors.Red;
    private double _thickness = 4.0;

    public RecordingDrawingWindow()
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
        ConfigureLayered();
        SetClickThrough(true);
    }

    public IntPtr Hwnd => _hwnd;

    public void MoveTo(int x, int y, int w, int h)
    {
        _appWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    public void SetColor(Color color) => _color = color;
    public void SetThickness(double t) => _thickness = System.Math.Max(1, t);

    public void SetActive(bool active)
    {
        SetClickThrough(!active);
    }

    public void ClearAll()
    {
        foreach (var s in _strokes) DrawCanvas.Children.Remove(s);
        _strokes.Clear();
    }

    private void ConfigureLayered()
    {
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        SetLayeredWindowAttributes(_hwnd, 0x00FF00FF, 0, LWA_COLORKEY);
    }

    private void SetClickThrough(bool clickThrough)
    {
        _clickThrough = clickThrough;
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = clickThrough ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
        SetLayeredWindowAttributes(_hwnd, 0x00FF00FF, 0, LWA_COLORKEY);
    }

    private void OnPointerPressedHandler(object sender, PointerRoutedEventArgs e)
    {
        if (_clickThrough) return;
        var p = e.GetCurrentPoint(DrawCanvas);
        if (p.Properties.IsRightButtonPressed)
        {
            ClearAll();
            e.Handled = true;
            return;
        }
        if (!p.Properties.IsLeftButtonPressed) return;
        _drawing = true;
        _active = new Polyline
        {
            Stroke = new SolidColorBrush(_color),
            StrokeThickness = _thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        _active.Points.Add(p.Position);
        DrawCanvas.Children.Add(_active);
        DrawCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMovedHandler(object sender, PointerRoutedEventArgs e)
    {
        if (!_drawing || _active == null) return;
        _active.Points.Add(e.GetCurrentPoint(DrawCanvas).Position);
    }

    private void OnPointerReleasedHandler(object sender, PointerRoutedEventArgs e)
    {
        if (!_drawing) return;
        _drawing = false;
        if (_active != null) _strokes.Add(_active);
        _active = null;
        DrawCanvas.ReleasePointerCapture(e.Pointer);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, int dwFlags);
}
