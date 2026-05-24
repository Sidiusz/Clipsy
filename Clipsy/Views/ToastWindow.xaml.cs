using System;
using System.Runtime.InteropServices;
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

public sealed partial class ToastWindow : Window
{
    private static readonly Color s_green = Color.FromArgb(0xFF, 0x23, 0xA5, 0x5A);
    private static readonly Color s_red   = Color.FromArgb(0xFF, 0xF2, 0x3F, 0x42);
    private static readonly Color s_blue  = Color.FromArgb(0xFF, 0x3B, 0x82, 0xF6);
    private static readonly Color s_amber = Color.FromArgb(0xFF, 0xF0, 0xB2, 0x32);

    private const int ToastW      = 380;
    private const int ToastH      = 80;
    private const int ToastGap    = 8;
    private const int ToastMargin = 16;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly Action? _action1;
    private readonly Action? _action2;
    private DispatcherTimer? _dismissTimer;
    private bool _isHovered;
    private bool _fadeInDone;
    private bool _isFadingOut;

    public ToastWindow(ToastService.ToastOptions opts)
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _action1 = opts.Action1Callback;
        _action2 = opts.Action2Callback;

        ConfigureWindow();
        ApplyOptions(opts);
        ThemeService.Register(Content as FrameworkElement);
        StartDismissTimer();
    }

    // ── Public API ──────────────────────────────────────────────

    internal void PositionAtSlot(int index)
    {
        double scale = DpiScale();
        int w = (int)(ToastW * scale);
        int h = (int)(ToastH * scale);
        int gap = (int)(ToastGap * scale);
        int margin = (int)(ToastMargin * scale);

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(_hwnd, MONITOR_DEFAULTTOPRIMARY), ref mi);

        int x = mi.rcWork.right - w - margin;
        int y = mi.rcWork.bottom - h - margin - index * (h + gap);

        _appWindow.MoveAndResize(new RectInt32(x, y, w, h));
        _appWindow.Show(false);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        if (!_fadeInDone)
        {
            _fadeInDone = true;
            BeginFadeIn();
        }
    }

    // ── Animation ────────────────────────────────────────────────

    private void BeginFadeIn()
    {
        var sb = new Storyboard();
        AddAnim(sb, CardBorder,   "Opacity", 0,  1,  200, new CubicEase     { EasingMode = EasingMode.EaseOut });
        AddAnim(sb, SlideTransform, "X",     20, 0,  200, new CubicEase     { EasingMode = EasingMode.EaseOut });
        sb.Begin();
    }

    private void BeginFadeOut()
    {
        if (_isFadingOut) return;
        _isFadingOut = true;
        _dismissTimer?.Stop();
        _dismissTimer = null;

        var sb = new Storyboard();
        AddAnim(sb, CardBorder,   "Opacity", 1, 0,  150, new QuadraticEase  { EasingMode = EasingMode.EaseIn });
        AddAnim(sb, SlideTransform, "X",     0, 20, 150, new QuadraticEase  { EasingMode = EasingMode.EaseIn });
        sb.Completed += (_, _) => { try { Close(); } catch { } };
        sb.Begin();
    }

    private static void AddAnim(Storyboard sb, DependencyObject target, string prop,
        double from, double to, double ms, EasingFunctionBase? ease = null)
    {
        var anim = new DoubleAnimation
        {
            From           = from,
            To             = to,
            Duration       = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, prop);
        sb.Children.Add(anim);
    }

    // ── Hover pause ──────────────────────────────────────────────

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = true;
        _dismissTimer?.Stop();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = false;
        StartDismissTimer();
    }

    // ── Setup ────────────────────────────────────────────────────

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
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        uint noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttributeU(_hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(uint));
    }

    private void ApplyOptions(ToastService.ToastOptions opts)
    {
        (Color accent, string glyph) = opts.Category switch
        {
            ToastCategory.Screenshot => (s_green, "\xE930"),
            ToastCategory.Error      => (s_red,   "\xEA39"),
            ToastCategory.Update     => (s_blue,  "\xE72C"),
            _                        => (s_amber, "\xE7BA"),
        };

        AccentBar.Fill = new SolidColorBrush(accent);
        LevelIcon.Glyph = glyph;
        LevelIcon.Foreground = new SolidColorBrush(accent);

        if (opts.Category == ToastCategory.Update)
            CardBorder.BorderBrush = new SolidColorBrush(s_blue);

        TitleText.Text = opts.Title;

        if (!string.IsNullOrEmpty(opts.Body))
        {
            BodyText.Text = opts.Body;
            BodyText.Visibility = Visibility.Visible;
        }

        SetupActionButton(Action1Btn, opts.Action1Icon, opts.Action1Tooltip, isPrimary: false);
        SetupActionButton(Action2Btn, opts.Action2Icon, opts.Action2Tooltip, isPrimary: opts.Action2IsPrimary);
    }

    private static void SetupActionButton(Button btn, string? icon, string? tooltip, bool isPrimary)
    {
        if (string.IsNullOrEmpty(icon)) return;

        btn.Content = new FontIcon { Glyph = icon, FontSize = 13 };
        if (isPrimary)
            btn.Style = (Style)Application.Current.Resources["ClipsyButtonPrimary"];
        if (!string.IsNullOrEmpty(tooltip))
            ToolTipService.SetToolTip(btn, tooltip);
        btn.Visibility = Visibility.Visible;
    }

    private void StartDismissTimer()
    {
        _dismissTimer?.Stop();
        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _dismissTimer.Tick += (_, _) => Dismiss();
        _dismissTimer.Start();
    }

    private void Dismiss()
    {
        _dismissTimer?.Stop();
        if (_isHovered) return;
        BeginFadeOut();
    }

    // ── Button handlers ──────────────────────────────────────────

    private void OnAction1Click(object sender, RoutedEventArgs e)
    {
        BeginFadeOut();
        try { _action1?.Invoke(); } catch (Exception ex) { Diagnostics.Log("ToastWindow.Action1", ex); }
    }

    private void OnAction2Click(object sender, RoutedEventArgs e)
    {
        BeginFadeOut();
        try { _action2?.Invoke(); } catch (Exception ex) { Diagnostics.Log("ToastWindow.Action2", ex); }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => BeginFadeOut();

    // ── Helpers ──────────────────────────────────────────────────

    private double DpiScale()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = GetDpiForSystem();
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    // ── Win32 ────────────────────────────────────────────────────

    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private const int GWL_STYLE   = -16;
    private const int GWL_EXSTYLE = -20;

    private const uint WS_POPUP       = 0x80000000;
    private const uint WS_CAPTION     = 0x00C00000;
    private const uint WS_THICKFRAME  = 0x00040000;
    private const uint WS_MINIMIZEBOX = 0x00020000;
    private const uint WS_MAXIMIZEBOX = 0x00010000;
    private const uint WS_SYSMENU     = 0x00080000;

    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int SWP_NOMOVE      = 0x0002;
    private const int SWP_NOSIZE      = 0x0001;
    private const int SWP_NOZORDER    = 0x0004;
    private const int SWP_NOACTIVATE  = 0x0010;
    private const int SWP_FRAMECHANGED = 0x0020;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR             = 34;
    private const uint DWMWA_COLOR_NONE              = 0xFFFFFFFE;
    private const int MONITOR_DEFAULTTOPRIMARY       = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int  cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern int   GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern int   SetWindowLong(IntPtr h, int n, int v);
    [DllImport("user32.dll")] private static extern bool  SetWindowPos(IntPtr h, IntPtr z, int x, int y, int cx, int cy, int flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
    [DllImport("user32.dll")] private static extern bool  GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern uint  GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern uint  GetDpiForSystem();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeU(IntPtr h, int attr, ref uint value, int size);
}
