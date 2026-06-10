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
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Clipsy.Views;

public enum TrayUpdateStatus { Idle, Checking, Available, UpToDate, Failed }

public sealed partial class TrayMenuWindow : Window
{
    public event Action? CaptureClicked;
    public event Action? OpenScreenshotsFolderClicked;
    public event Action? OpenVideoFolderClicked;
    public event Action? SettingsClicked;
    public event Action? UpdateStatusClicked;
    public event Action? ExitClicked;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private bool _hiding;
    private bool _closed;
    private TrayUpdateStatus _updateStatus = TrayUpdateStatus.Idle;
    private EventHandler<object>? _fadeHandler;

    private static readonly SolidColorBrush s_transparent = new(Colors.Transparent);

    private record ItemParts(UIElement Icon, TextBlock Label, TextBlock? Shortcut);
    private readonly Dictionary<Grid, ItemParts> _parts = new();

    private const int MenuW      = 264;
    private const int MenuH_Base = 266;   // without update row
    private const int MenuH_Row  = 20;    // extra height when update row is visible

    public TrayMenuWindow()
    {
        InitializeComponent();
        ThemeService.Register(Content as FrameworkElement);
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
        MapItemParts();
        ApplyLocalization();

        Activated += OnActivated;
        Closed += (_, _) => _closed = true;
        WarmUp();

        // Re-localize when language flips so the next tray-menu open shows
        // the new strings without an app restart.
        SettingsService.Instance.SettingsChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged()
    {
        try { ApplyLocalization(); }
        catch (Exception ex) { Diagnostics.Log("TrayMenuWindow.OnSettingsChanged", ex); }
    }

    // ────────────────────────────────────────────────────────
    // Public API
    // ────────────────────────────────────────────────────────

    public void ShowAtCursor()
    {
        if (_closed) return;

        double scale = GetDpiScale();
        int w = (int)(MenuW * scale);
        int h = (int)(CurrentMenuH * scale);

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

        // Start fully transparent (layered alpha 0) so the uncloak reveals
        // nothing until the fade ramps it up.
        SetLayeredWindowAttributes(_hwnd, 0, 0, LWA_ALPHA);

        try
        {
            _appWindow.MoveAndResize(new RectInt32(x, y, w, h));
        }
        catch (Exception ex)
        {
            // AppWindow handle can go stale (0x80070578) after certain Win32 events.
            // Log and bail — preferable to crashing the app via AppDomain.UnhandledException.
            Diagnostics.Log("TrayMenuWindow.ShowAtCursor MoveAndResize", ex);
            return;
        }
        Cloak(false); // reveal the already-composed frame — no black swapchain flash
        Activate();
        // H.NotifyIcon's right-click handler runs inside a tray nested
        // message pump; without an explicit foreground request the window
        // shows but Windows never marks it active, so the Deactivated event
        // never fires when the user clicks elsewhere and the menu sticks.
        SetForegroundWindow(_hwnd);
        PlayOpenAnimation();
    }

    // Show the window once, off-screen and cloaked, so WinUI composes its
    // first frame into the swapchain. From then on the surface stays warm and
    // every open is just an uncloak — the black "first paint" never recurs.
    private void WarmUp()
    {
        try
        {
            Cloak(true);
            _appWindow.MoveAndResize(new RectInt32(-32000, -32000, MenuW, CurrentMenuH));
            Activate();
        }
        catch (Exception ex) { Diagnostics.Log("TrayMenuWindow.WarmUp", ex); }
    }

    // DWM cloaking hides the window from the compositor WITHOUT destroying its
    // swapchain content (unlike AppWindow.Hide). Cloaked windows are also not
    // hit-testable, so this fully replaces Hide for dismissal.
    private void Cloak(bool on)
    {
        int v = on ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref v, sizeof(int));
    }

    // Uniform whole-window fade via the layered-window alpha (0→255 over
    // 120ms, ease-out). Fades the entire composited frame — backdrop and
    // content together — so there's no content-scaling artifact and, because
    // layered-transparent shows the desktop (not black), no flash.
    private void PlayOpenAnimation()
    {
        StopFade();
        var start = DateTime.UtcNow;
        const double durMs = 120.0;
        _fadeHandler = (_, _) =>
        {
            double t = Math.Min((DateTime.UtcNow - start).TotalMilliseconds / durMs, 1.0);
            double eased = 1.0 - Math.Pow(1.0 - t, 3); // ease-out cubic
            SetLayeredWindowAttributes(_hwnd, 0, (byte)(eased * 255), LWA_ALPHA);
            if (t >= 1.0) StopFade();
        };
        CompositionTarget.Rendering += _fadeHandler;
    }

    private void StopFade()
    {
        if (_fadeHandler != null)
        {
            CompositionTarget.Rendering -= _fadeHandler;
            _fadeHandler = null;
        }
    }

    public void SetUpdateStatus(TrayUpdateStatus status, string? newVersion = null)
    {
        _updateStatus = status;

        var text3   = ThemeService.GetBrush("ClipsyText3Brush", Content as FrameworkElement);
        var green   = ThemeService.GetBrush("ClipsySuccessBrush", Content as FrameworkElement);
        var red     = ThemeService.GetBrush("ClipsyDangerBrush", Content as FrameworkElement);
        var warning = ThemeService.GetBrush("ClipsyWarningBrush", Content as FrameworkElement);

        switch (status)
        {
            case TrayUpdateStatus.Checking:
                UpdateRow.Visibility     = Visibility.Visible;
                UpdateRow.IsHitTestVisible = false;
                UpdateRowIcon.Glyph      = "";
                UpdateRowIcon.Foreground = text3;
                UpdateRowText.Text       = Strings.Get("TrayUpdateChecking");
                UpdateRowText.Foreground = text3;
                break;

            case TrayUpdateStatus.Available:
                UpdateRow.Visibility     = Visibility.Visible;
                UpdateRow.IsHitTestVisible = true;
                UpdateRowIcon.Glyph      = "";
                UpdateRowIcon.Foreground = green;
                UpdateRowText.Text       = newVersion != null
                    ? $"v{newVersion} — {Strings.Get("TrayUpdateAvailable")}"
                    : Strings.Get("TrayUpdateAvailable");
                UpdateRowText.Foreground = green;
                break;

            case TrayUpdateStatus.Failed:
                UpdateRow.Visibility     = Visibility.Visible;
                UpdateRow.IsHitTestVisible = true;
                UpdateRowIcon.Glyph      = "";
                UpdateRowIcon.Foreground   = warning;
                UpdateRowText.Text         = string.Empty;

                break;

            default: // UpToDate / Idle
                UpdateRow.Visibility = Visibility.Collapsed;
                break;
        }
    }

    // ────────────────────────────────────────────────────────
    // Window setup
    // ────────────────────────────────────────────────────────

    // Update button now sits beside the header text (right column), not
    // below the version — header height stays constant regardless of status.
    private int CurrentMenuH => MenuH_Base;

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

        // WS_EX_LAYERED makes DWM compose the window to an off-screen buffer
        // and present it atomically. Without it, WinUI shows the bare HWND
        // (black background erase) for a frame before the XAML content paints
        // — the "black backdrop" flash. LWA_ALPHA 255 keeps it fully opaque.
        int exStyle = GetWindowLong(_hwnd, GWL_EXSTYLE);
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW | WS_EX_LAYERED);
        SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        uint noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttributeU(_hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(uint));
    }

    private void MapItemParts()
    {
        _parts[CaptureRow]          = new(CaptureIcon,          CaptureTxt,          CaptureShortcut);
        _parts[ScreenshotsFolderRow]= new(ScreenshotsFolderIcon,ScreenshotsFolderTxt,null);
        _parts[VideoFolderRow]      = new(VideoFolderIcon,      VideoFolderTxt,      null);
        _parts[SettingsRow]         = new(SettingsIcon,         SettingsTxt,         SettingsShortcut);
        _parts[ExitRow]             = new(ExitIcon,             ExitTxt,             null);
    }

    private void ApplyLocalization()
    {
        CaptureTxt.Text          = Strings.Get("TrayCapture");
        ScreenshotsFolderTxt.Text= Strings.Get("TrayOpenScreenshots");
        VideoFolderTxt.Text      = Strings.Get("TrayOpenVideos");
        SettingsTxt.Text         = Strings.Get("TraySettings");
        ExitTxt.Text             = Strings.Get("TrayExit");
        HeaderVersion.Text       = $"v{UpdateService.CurrentVersion()}";
    }

    // ────────────────────────────────────────────────────────
    // Hide on deactivation
    // ────────────────────────────────────────────────────────

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_closed) return;
        if (e.WindowActivationState == WindowActivationState.Deactivated && !_hiding)
            HideMenu();
    }

    private void HideMenu()
    {
        if (_hiding || _closed) return;
        _hiding = true;
        StopFade();
        Cloak(true); // keep the swapchain warm; do not tear it down with Hide()
        foreach (var row in _parts.Keys)
            SetHover(row, false);
        _hiding = false;
    }

    // ────────────────────────────────────────────────────────
    // Hover
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
            g.Background = ThemeService.GetBrush("ClipsyAccentPressedBrush", Content as FrameworkElement);
    }

    private void OnItemPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid g) SetHover(g, true);
    }

    private void SetHover(Grid row, bool on)
    {
        if (!_parts.TryGetValue(row, out var p)) return;

        var accent    = ThemeService.GetBrush("ClipsyAccentBrush", Content as FrameworkElement);
        var black     = new SolidColorBrush(Colors.Black);
        var textBrush = ThemeService.GetBrush("ClipsyTextBrush", Content as FrameworkElement);
        var iconBrush = ThemeService.GetBrush("ClipsyText2Brush", Content as FrameworkElement);
        var hintBrush = ThemeService.GetBrush("ClipsyText3Brush", Content as FrameworkElement);

        row.Background     = on ? accent : s_transparent;
        p.Label.Foreground = on ? black  : textBrush;
        if (p.Shortcut != null) p.Shortcut.Foreground = on ? black : hintBrush;

        if (p.Icon is FontIcon fi)
            fi.Foreground = on ? black : iconBrush;
    }

    // ────────────────────────────────────────────────────────
    // Click handlers
    // ────────────────────────────────────────────────────────

    private void OnCaptureClick(object s, TappedRoutedEventArgs e)
        { HideMenu(); CaptureClicked?.Invoke(); }

    private void OnOpenScreenshotsFolderClick(object s, TappedRoutedEventArgs e)
        { HideMenu(); OpenScreenshotsFolderClicked?.Invoke(); }

    private void OnOpenVideoFolderClick(object s, TappedRoutedEventArgs e)
        { HideMenu(); OpenVideoFolderClicked?.Invoke(); }

    private void OnSettingsClick(object s, TappedRoutedEventArgs e)
        { HideMenu(); SettingsClicked?.Invoke(); }

    private void OnExitClick(object s, TappedRoutedEventArgs e)
        { HideMenu(); ExitClicked?.Invoke(); }

    private void OnUpdateRowTapped(object s, TappedRoutedEventArgs e)
        { HideMenu(); UpdateStatusClicked?.Invoke(); }

    // ────────────────────────────────────────────────────────
    // Win32
    // ────────────────────────────────────────────────────────

    private double GetDpiScale()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    private const int   GWL_STYLE    = -16;
    private const int   GWL_EXSTYLE  = -20;
    private const uint  WS_POPUP     = 0x80000000;
    private const uint  WS_CAPTION   = 0x00C00000;
    private const uint  WS_THICKFRAME   = 0x00040000;
    private const uint  WS_MINIMIZEBOX  = 0x00020000;
    private const uint  WS_MAXIMIZEBOX  = 0x00010000;
    private const uint  WS_SYSMENU   = 0x00080000;
    private const int   WS_EX_TOOLWINDOW = 0x00000080;
    private const int   WS_EX_LAYERED     = 0x00080000;
    private const int   LWA_ALPHA         = 0x00000002;
    private const uint  SWP_NOMOVE      = 0x0002;
    private const uint  SWP_NOSIZE      = 0x0001;
    private const uint  SWP_NOZORDER    = 0x0004;
    private const uint  SWP_NOACTIVATE  = 0x0010;
    private const uint  SWP_FRAMECHANGED = 0x0020;
    private const int   DWMWA_CLOAK = 13;
    private const int   DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int   DWMWA_BORDER_COLOR = 34;
    private const uint  DWMWA_COLOR_NONE = 0xFFFFFFFE;
    private const uint  MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]  private static extern int   GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")]  private static extern int   SetWindowLong(IntPtr h, int n, int v);
    [DllImport("user32.dll")]  private static extern bool  SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")]  private static extern bool  GetCursorPos(out POINT pt);
    [DllImport("user32.dll")]  private static extern bool  SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, int dwFlags);
    [DllImport("user32.dll")]  private static extern uint  GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")]  private static extern IntPtr MonitorFromPoint(POINT pt, uint f);
    [DllImport("user32.dll")]  private static extern bool  GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("dwmapi.dll")]  private static extern int   DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeU(IntPtr h, int attr, ref uint v, int size);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT  { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int  cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
