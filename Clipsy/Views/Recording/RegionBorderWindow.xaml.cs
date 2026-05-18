using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Clipsy.Views.Recording;

/// <summary>
/// A topmost, frameless ring-shaped window drawn in red around the
/// recording region. The interior is clipped away with SetWindowRgn so
/// clicks and pointer events fall through to whatever is being recorded.
/// </summary>
public sealed partial class RegionBorderWindow : Window
{
    private const int BorderThicknessPixels = 3;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private int _curX, _curY, _curW, _curH;

    public IntPtr Hwnd => _hwnd;

    public RegionBorderWindow()
    {
        InitializeComponent();
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
    }

    public void MoveTo(int x, int y, int w, int h)
    {
        _curX = x; _curY = y; _curW = w; _curH = h;
        _appWindow.MoveAndResize(new RectInt32(x, y, w, h));
        ApplyRing(w, h);
    }

    private void ApplyRing(int w, int h)
    {
        var outer = CreateRectRgn(0, 0, w, h);
        var inner = CreateRectRgn(BorderThicknessPixels, BorderThicknessPixels,
            w - BorderThicknessPixels, h - BorderThicknessPixels);
        var ring = CreateRectRgn(0, 0, 0, 0);
        CombineRgn(ring, outer, inner, RGN_DIFF);
        SetWindowRgn(_hwnd, ring, true);
        DeleteObject(outer);
        DeleteObject(inner);
    }

    private void ApplyTransparencyClickThrough()
    {
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW);
        SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int LWA_ALPHA = 0x00000002;
    private const int RGN_DIFF = 4;

    [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, int dwFlags);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);
    [DllImport("gdi32.dll")] static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
    [DllImport("gdi32.dll")] static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
}
