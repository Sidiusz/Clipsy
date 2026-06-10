using System;
using System.Runtime.InteropServices;
using Clipsy.Services;
using Microsoft.UI;
using Windows.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace Clipsy.Views.Recording;

/// <summary>
/// Floating color-picker window opened by the recording HUD.
/// Runs in its own HWND so it isn't clipped by the HUD's layered window.
/// Excluded from screen capture via SetWindowDisplayAffinity.
/// </summary>
public sealed partial class HudColorPickerWindow : Window
{
    // Width = StackPanel 240 + 2×12 padding. Height is measured dynamically via
    // Root.SizeChanged — the window appears off-screen first, then repositions
    // once WinUI has laid out the ColorPicker at its natural height.
    private const int LogicalW  = 268;
    private const int LogicalMaxH = 700; // generous off-screen height; trimmed by SizeChanged
    private const int AnchorGap = 6;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private Windows.UI.Color _colorOnOpen;
    private int _anchorX, _anchorY, _anchorW, _anchorH;
    private bool _repositioning;

    public event Action<byte, byte, byte>? ColorConfirmed;
    public event Action? ColorCanceled;

    public HudColorPickerWindow()
    {
        InitializeComponent();
        if (Content is FrameworkElement fe) fe.RequestedTheme = ElementTheme.Dark; // recording chrome pinned dark
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
        Activated += OnActivated;
        Root.SizeChanged += OnRootSizeChanged;

        ColorPickerCtl.ColorConfirmed += c  => { ColorConfirmed?.Invoke(c.R, c.G, c.B); HideWindow(); };
        ColorPickerCtl.ColorCanceled  += () => { ColorCanceled?.Invoke(); HideWindow(); };
    }

    // ──────────────────────────────────────────────────────────────────
    // Public API
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Show the picker floating near the given anchor rectangle (screen physical pixels).
    /// The window first appears off-screen; OnRootSizeChanged repositions it once
    /// WinUI has measured the ColorPicker's natural height.
    /// </summary>
    public void ShowAt(Windows.UI.Color currentColor, int anchorX, int anchorY, int anchorW, int anchorH)
    {
        _colorOnOpen = currentColor;
        _anchorX = anchorX; _anchorY = anchorY; _anchorW = anchorW; _anchorH = anchorH;
        ColorPickerCtl.Color = currentColor;

        double scale = GetDpiScale();
        int w = (int)(LogicalW * scale);
        int h = (int)(LogicalMaxH * scale);
        // Park off-screen; SizeChanged will reposition once layout is known.
        _appWindow.MoveAndResize(new RectInt32(-32000, -32000, w, h));
        Activate();
        SetForegroundWindow(_hwnd);
    }

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_repositioning || e.NewSize.Height < 10) return;

        double scale = GetDpiScale();
        int w = (int)Math.Ceiling(e.NewSize.Width  * scale);
        int h = (int)Math.Ceiling(e.NewSize.Height * scale);

        int x = _anchorX + _anchorW / 2 - w / 2;
        int y = _anchorY - h - AnchorGap;

        var anchorRect = new RECT(_anchorX, _anchorY, _anchorX + _anchorW, _anchorY + _anchorH);
        IntPtr hMon = MonitorFromRect(ref anchorRect, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMon, ref mi);
        var work = mi.rcWork;

        if (y < work.top) y = _anchorY + _anchorH + AnchorGap;
        if (x + w > work.right)  x = work.right  - w;
        if (x < work.left)       x = work.left;

        _repositioning = true;
        _appWindow.MoveAndResize(new RectInt32(x, y, w, h));
        _repositioning = false;
    }

    public void HideWindow() => _appWindow.Hide();

    // ──────────────────────────────────────────────────────────────────
    // Event handlers
    // ──────────────────────────────────────────────────────────────────

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
            HideWindow();
    }

    // ──────────────────────────────────────────────────────────────────
    // Window setup
    // ──────────────────────────────────────────────────────────────────

    private void ConfigureWindow()
    {
        if (_appWindow.Presenter is OverlappedPresenter op)
        {
            op.SetBorderAndTitleBar(false, false);
            op.IsResizable   = false;
            op.IsMaximizable = false;
            op.IsMinimizable = false;
            op.IsAlwaysOnTop = true;
        }
        _appWindow.IsShownInSwitchers = false;

        var style = (uint)GetWindowLong(_hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        style |= WS_POPUP;
        SetWindowLong(_hwnd, GWL_STYLE, unchecked((int)style));

        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        uint noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttributeU(_hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(uint));

        // Exclude from screen capture so the picker doesn't appear in the video.
        SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);
    }

    private double GetDpiScale()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    // ──────────────────────────────────────────────────────────────────
    // Win32
    // ──────────────────────────────────────────────────────────────────

    private const int    GWL_STYLE    = -16;
    private const int    GWL_EXSTYLE  = -20;
    private const uint   WS_POPUP        = 0x80000000;
    private const uint   WS_CAPTION      = 0x00C00000;
    private const uint   WS_THICKFRAME   = 0x00040000;
    private const uint   WS_MINIMIZEBOX  = 0x00020000;
    private const uint   WS_MAXIMIZEBOX  = 0x00010000;
    private const uint   WS_SYSMENU      = 0x00080000;
    private const int    WS_EX_TOOLWINDOW = 0x00000080;
    private const uint   SWP_NOMOVE      = 0x0002;
    private const uint   SWP_NOSIZE      = 0x0001;
    private const uint   SWP_NOZORDER    = 0x0004;
    private const uint   SWP_NOACTIVATE  = 0x0010;
    private const uint   SWP_FRAMECHANGED = 0x0020;
    private const int    DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int    DWMWA_BORDER_COLOR = 34;
    private const uint   DWMWA_COLOR_NONE   = 0xFFFFFFFE;
    private const uint   MONITOR_DEFAULTTONEAREST = 2;
    private const uint   WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")]  private static extern int   GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")]  private static extern int   SetWindowLong(IntPtr h, int n, int v);
    [DllImport("user32.dll")]  private static extern bool  SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")]  private static extern bool  SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]  private static extern uint  GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")]  private static extern bool  SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    [DllImport("user32.dll")]  private static extern IntPtr MonitorFromRect(ref RECT lprc, uint dwFlags);
    [DllImport("user32.dll")]  private static extern bool  GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("dwmapi.dll")]  private static extern int   DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeU(IntPtr h, int attr, ref uint v, int size);

    [StructLayout(LayoutKind.Sequential)] private struct RECT
    {
        public int left, top, right, bottom;
        public RECT(int l, int t, int r, int b) { left=l; top=t; right=r; bottom=b; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int  cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
