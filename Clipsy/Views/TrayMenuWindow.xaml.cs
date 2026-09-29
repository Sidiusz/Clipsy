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
    private EventHandler<object>? _fadeHandler;

    private static readonly SolidColorBrush s_transparent = new(Colors.Transparent);

    private record ItemParts(UIElement Icon, TextBlock Label, TextBlock? Shortcut);
    private readonly Dictionary<Grid, ItemParts> _parts = new();

    private const int MenuW      = 264;
    private const int MenuH_Base = 266;

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
        Closed += (_, _) => PrepareForShutdown();
        RootGrid.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { HideMenu(); e.Handled = true; }
        };
        WarmUp();

        // Re-localize when language flips so the next tray-menu open shows
        // the new strings without an app restart.
        SettingsService.Instance.SettingsChanged += OnSettingsChanged;

        UpdateManager.StateChanged += RenderUpdate;
        RenderUpdate();
    }

    private void OnSettingsChanged()
    {
        try
        {
            ApplyLocalization();
            // Rows pin resting brushes imperatively (overriding ThemeResource),
            // so re-apply them once the new theme reaches ActualTheme.
            DispatcherQueue.TryEnqueue(RefreshRowColors);
        }
        catch (Exception ex) { Diagnostics.Log("TrayMenuWindow.OnSettingsChanged", ex); }
    }

    private void RefreshRowColors()
    {
        try
        {
            foreach (var row in _parts.Keys)
                SetHover(row, false);
            RenderUpdate();
        }
        catch (Exception ex) { Diagnostics.Log("TrayMenuWindow.RefreshRowColors", ex); }
    }

    // ─── Public API ───

    public void PrepareForShutdown()
    {
        if (_closed) return;
        _closed = true;
        StopFade();
        Activated -= OnActivated;
        SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
        UpdateManager.StateChanged -= RenderUpdate;
    }

    public void ShowAtCursor()
    {
        if (_closed) return;

        GetCursorPos(out POINT pt);

        IntPtr hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        // Scale for the monitor under the cursor, not the one the menu last sat on.
        double scale = GetDpiForMonitor(hMon, 0, out uint dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : GetDpiScale();
        int w = (int)Math.Round(MenuW * scale);
        int h = (int)Math.Round(MeasureMenuHeight() * scale);

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
            var rect = new RectInt32(x, y, w, h);
            _appWindow.MoveAndResize(rect);
            // Crossing to a monitor with another DPI rescales the window once; re-apply the exact rect.
            if (_appWindow.Size.Width != w || _appWindow.Size.Height != h) _appWindow.MoveAndResize(rect);
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
        // The tray runs a nested message pump; without an explicit foreground
        // request the window never goes active, so Deactivated never fires.
        SetForegroundWindow(_hwnd);
        PlayOpenAnimation();
    }

    // Show once off-screen and cloaked so WinUI composes the first frame; from
    // then on every open is just an uncloak, no black first paint.
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

    // DWM cloak hides the window without destroying swapchain content (unlike
    // Hide) and isn't hit-testable, so it fully replaces Hide for dismissal.
    private void Cloak(bool on)
    {
        int v = on ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref v, sizeof(int));
    }

    // Whole-window fade via layered alpha (0→255, 120ms ease-out): fades the
    // composited frame uniformly, and shows the desktop (not black), so no flash.
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


    // Header update button from shared UpdateManager state: download → percent → install.
    private void RenderUpdate()
    {
        try
        {
            var info = UpdateManager.Info;
            var phase = UpdateManager.Phase;
            string current = $"v{UpdateService.CurrentVersion()}";
            bool pending = info != null && phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Ready;
            HeaderVersion.Text = pending ? $"{current} → {info!.Version}" : current;

            UpdateBtn.IsEnabled = phase != UpdatePhase.Downloading;
            switch (phase)
            {
                case UpdatePhase.Available when info != null:
                    UpdateBtn.Content = string.Format(Strings.Get("UpdBtnDownloadVer"), info.Version);
                    break;
                case UpdatePhase.Downloading:
                    UpdateBtn.Content = $"{(int)(UpdateManager.Progress * 100)}%";
                    break;
                case UpdatePhase.Ready:
                    UpdateBtn.Content = Strings.Get("UpdBtnInstallShort");
                    break;
                case UpdatePhase.Failed:
                    UpdateBtn.Content = Strings.Get("UpdBtnRetry");
                    break;
                default: // None / Checking / UpToDate
                    UpdateBtn.Visibility = Visibility.Collapsed;
                    return;
            }
            UpdateBtn.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(UpdateBtn, phase switch
            {
                UpdatePhase.Ready => Strings.Get("TrayUpdateInstall"),
                UpdatePhase.Failed => Strings.Get("TrayUpdateFailed"),
                _ => null,
            });
        }
        catch (Exception ex) { Diagnostics.Log("TrayMenuWindow.RenderUpdate", ex); }
    }

    // ─── Window setup ───

    private int CurrentMenuH => MenuH_Base;

    // Honors text scaling (Accessibility > Text size), which grows rows past the design height.
    private double MeasureMenuHeight()
    {
        try
        {
            RootGrid.Measure(new Windows.Foundation.Size(MenuW, double.PositiveInfinity));
            return Math.Max(MenuH_Base, Math.Ceiling(RootGrid.DesiredSize.Height));
        }
        catch { return MenuH_Base; }
    }

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

        // WS_EX_LAYERED composes off-screen and presents atomically, hiding the
        // bare-HWND black erase before first paint; LWA_ALPHA 255 keeps it opaque.
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
        _parts[SettingsRow]         = new(SettingsIcon,         SettingsTxt,         null);
        _parts[ExitRow]             = new(ExitIcon,             ExitTxt,             null);
    }

    private void ApplyLocalization()
    {
        CaptureTxt.Text          = Strings.Get("TrayCapture");
        ScreenshotsFolderTxt.Text= Strings.Get("TrayOpenScreenshots");
        VideoFolderTxt.Text      = Strings.Get("TrayOpenVideos");
        SettingsTxt.Text         = Strings.Get("TraySettings");
        ExitTxt.Text             = Strings.Get("TrayExit");
        CaptureShortcut.Text     = FormatBinding(SettingsService.Instance.Settings.HotkeyCapture);
        RenderUpdate();
    }

    private static string FormatBinding(string? binding)
    {
        if (string.IsNullOrWhiteSpace(binding)) return string.Empty;
        return binding.Replace("PrintScreen", "PrtSc", StringComparison.OrdinalIgnoreCase)
                      .Replace("Snapshot", "PrtSc", StringComparison.OrdinalIgnoreCase);
    }

    // ─── Hide on deactivation ───

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

    // ─── Hover ───

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
        if (_closed) return;
        if (sender is Grid g)
            g.Background = ThemeService.GetBrush("ClipsyAccentPressedBrush", Content as FrameworkElement);
    }

    private void OnItemPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid g) SetHover(g, true);
    }

    private void SetHover(Grid row, bool on)
    {
        if (_closed || !_parts.TryGetValue(row, out var p)) return;

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

    // ─── Click handlers ───

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

    private void OnUpdateClick(object s, RoutedEventArgs e)
        => UpdateStatusClicked?.Invoke(); // menu stays open to show download progress

    // ─── Win32 ───

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
    [DllImport("shcore.dll")]  private static extern int   GetDpiForMonitor(IntPtr hMon, int type, out uint dpiX, out uint dpiY);
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
