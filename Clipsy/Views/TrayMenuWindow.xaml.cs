using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Clipsy.Localization;
using Clipsy.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Clipsy.Views;

public sealed partial class TrayMenuWindow : Window
{
    public event Action? CaptureClicked;
    public event Action? RecordClicked;
    public event Action? OpenFolderClicked;
    public event Action? SettingsClicked;
    public event Action? AboutClicked;
    public event Action? ExitClicked;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private bool _hiding;

    // Transparent brush keeps Grids hittable (null background is not hittable)
    private static readonly SolidColorBrush s_transparent = new(Colors.Transparent);

    private record ItemParts(UIElement Icon, TextBlock Label, TextBlock? Shortcut);
    private readonly Dictionary<Grid, ItemParts> _parts = new();

    // ── Logical dimensions (scaled to physical at show time) ──
    private const int MenuW = 264;
    private const int MenuH = 304;

    public TrayMenuWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
        MapItemParts();
        ApplyLocalization();

        Activated += OnActivated;
    }

    // ────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────

    public void ShowAtCursor()
    {
        double scale = GetDpiScale();
        int w = (int)(MenuW * scale);
        int h = (int)(MenuH * scale);

        GetCursorPos(out POINT pt);

        IntPtr hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(hMon, ref mi);
        var work = mi.rcWork;

        int x = pt.x - w / 2;
        int y = pt.y - h - 4;

        if (x + w > work.right)  x = work.right - w;
        if (x < work.left)       x = work.left;
        if (y < work.top)        y = pt.y + 4;

        _appWindow.MoveAndResize(new RectInt32(x, y, w, h));
        Activate();
    }

    // ────────────────────────────────────────────────────────
    // Window setup
    // ────────────────────────────────────────────────────────

    private void ConfigureWindow()
    {
        if (_appWindow.Presenter is OverlappedPresenter op)
        {
            op.SetBorderAndTitleBar(false, false);
            op.IsResizable    = false;
            op.IsMaximizable  = false;
            op.IsMinimizable  = false;
            op.IsAlwaysOnTop  = true;
        }
        _appWindow.IsShownInSwitchers = false;

        // Remove WS_CAPTION / WS_THICKFRAME; add WS_POPUP
        var style = (uint)GetWindowLong(_hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        style |= WS_POPUP;
        SetWindowLong(_hwnd, GWL_STYLE, unchecked((int)style));

        // WS_EX_TOOLWINDOW: hide from taskbar / Alt+Tab
        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // Win11 rounded corners (DWMWCP_ROUND = 2)
        int round = 2;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        // Remove DWM border colour
        uint noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttributeU(_hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(uint));
    }

    private void MapItemParts()
    {
        _parts[CaptureRow]  = new(CaptureIcon,  CaptureTxt,  CaptureShortcut);
        _parts[RecordRow]   = new(RecordDot,    RecordTxt,   RecordShortcut);
        _parts[FolderRow]   = new(FolderIcon,   FolderTxt,   null);
        _parts[SettingsRow] = new(SettingsIcon, SettingsTxt, SettingsShortcut);
        _parts[AboutRow]    = new(AboutIcon,    AboutTxt,    null);
        _parts[ExitRow]     = new(ExitIcon,     ExitTxt,     null);
    }

    private void ApplyLocalization()
    {
        CaptureTxt.Text  = Strings.Get("TrayCapture");
        RecordTxt.Text   = Strings.Get("TrayRecord");
        FolderTxt.Text   = Strings.Get("TrayOpenFolder");
        SettingsTxt.Text = Strings.Get("TraySettings");
        AboutTxt.Text    = Strings.Get("TrayAbout");
        ExitTxt.Text     = Strings.Get("TrayExit");
        HeaderStatus.Text = $"v{UpdateService.CurrentVersion()} · {Strings.Get("TrayReady")}";
    }

    // ────────────────────────────────────────────────────────
    // Hide on deactivation
    // ────────────────────────────────────────────────────────

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated && !_hiding)
            HideMenu();
    }

    private void HideMenu()
    {
        if (_hiding) return;
        _hiding = true;
        _appWindow.Hide();
        foreach (var row in _parts.Keys)
            SetHover(row, false);
        _hiding = false;
    }

    // ────────────────────────────────────────────────────────
    // Hover state
    // ────────────────────────────────────────────────────────

    private void OnItemPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid g) SetHover(g, true);
    }

    private void OnItemPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid g) SetHover(g, false);
    }

    private void OnItemPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid g)
            g.Background = (SolidColorBrush)Application.Current.Resources["ClipsyAccentPressedBrush"];
    }

    private void OnItemPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid g) SetHover(g, true);
    }

    private void SetHover(Grid row, bool on)
    {
        if (!_parts.TryGetValue(row, out var p)) return;

        var accent    = (SolidColorBrush)Application.Current.Resources["ClipsyAccentBrush"];
        var black     = new SolidColorBrush(Colors.Black);
        var textBrush = (SolidColorBrush)Application.Current.Resources["ClipsyTextBrush"];
        var iconBrush = (SolidColorBrush)Application.Current.Resources["ClipsyText2Brush"];
        var hintBrush = (SolidColorBrush)Application.Current.Resources["ClipsyText3Brush"];

        row.Background    = on ? accent       : s_transparent;
        p.Label.Foreground = on ? black        : textBrush;
        if (p.Shortcut != null) p.Shortcut.Foreground = on ? black : hintBrush;

        switch (p.Icon)
        {
            case FontIcon fi: fi.Foreground = on ? black : iconBrush; break;
            case Ellipse  el: el.Fill       = on ? black : iconBrush; break;
        }
    }

    // ────────────────────────────────────────────────────────
    // Click handlers
    // ────────────────────────────────────────────────────────

    private void OnCaptureClick(object s, TappedRoutedEventArgs e)    { HideMenu(); CaptureClicked?.Invoke(); }
    private void OnRecordClick(object s, TappedRoutedEventArgs e)     { HideMenu(); RecordClicked?.Invoke(); }
    private void OnOpenFolderClick(object s, TappedRoutedEventArgs e) { HideMenu(); OpenFolderClicked?.Invoke(); }
    private void OnSettingsClick(object s, TappedRoutedEventArgs e)   { HideMenu(); SettingsClicked?.Invoke(); }
    private void OnAboutClick(object s, TappedRoutedEventArgs e)      { HideMenu(); AboutClicked?.Invoke(); }
    private void OnExitClick(object s, TappedRoutedEventArgs e)       { HideMenu(); ExitClicked?.Invoke(); }

    // ────────────────────────────────────────────────────────
    // Win32 helpers
    // ────────────────────────────────────────────────────────

    private double GetDpiScale()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    // Win32 constants
    private const int    GWL_STYLE    = -16;
    private const int    GWL_EXSTYLE  = -20;
    private const uint   WS_POPUP     = 0x80000000;
    private const uint   WS_CAPTION   = 0x00C00000;
    private const uint   WS_THICKFRAME   = 0x00040000;
    private const uint   WS_MINIMIZEBOX = 0x00020000;
    private const uint   WS_MAXIMIZEBOX = 0x00010000;
    private const uint   WS_SYSMENU  = 0x00080000;
    private const int    WS_EX_TOOLWINDOW = 0x00000080;
    private const uint   SWP_NOMOVE   = 0x0002;
    private const uint   SWP_NOSIZE   = 0x0001;
    private const uint   SWP_NOZORDER = 0x0004;
    private const uint   SWP_NOACTIVATE = 0x0010;
    private const uint   SWP_FRAMECHANGED = 0x0020;
    private const int    DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int    DWMWA_BORDER_COLOR = 34;
    private const uint   DWMWA_COLOR_NONE = 0xFFFFFFFE;
    private const uint   MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]   private static extern int  GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")]   private static extern int  SetWindowLong(IntPtr h, int n, int v);
    [DllImport("user32.dll")]   private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]   private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")]   private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")]   private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")]   private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("dwmapi.dll")]   private static extern int  DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeU(IntPtr h, int attr, ref uint v, int size);

    [StructLayout(LayoutKind.Sequential)] private struct POINT  { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT   { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
