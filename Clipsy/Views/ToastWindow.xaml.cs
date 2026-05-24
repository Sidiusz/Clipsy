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
using Windows.Foundation;
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
    private const int MinH        = 50;
    private const int ToastGap    = 8;
    private const int ToastMargin = 16;
    private const int FadeInMs    = 200;
    private const int FadeOutMs   = 150;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly Action? _action1;
    private readonly Action? _action2;
    private DispatcherTimer? _dismissTimer;
    private DispatcherTimer? _alphaTimer;
    private bool _isHovered;
    private bool _fadeInDone;
    private bool _isFadingOut;

    public ToastWindow(ToastService.ToastOptions opts)
    {
        InitializeComponent();
        this.SystemBackdrop = null;
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _action1 = opts.Action1Callback;
        _action2 = opts.Action2Callback;

        ConfigureWindow();
        ApplyOptions(opts);
        ThemeService.Register(Content as FrameworkElement);
        SetLayeredWindowAttributes(_hwnd, 0, 0, LWA_ALPHA); // start invisible
        StartDismissTimer();
    }

    // ── Public API ──────────────────────────────────────────────

    internal void PositionAtSlot(int index)
    {
        double scale = DpiScale();
        int w = (int)(ToastW * scale);
        int h = ComputeHeightPx(scale);
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

    private int ComputeHeightPx(double scale)
    {
        try
        {
            CardBorder.Measure(new Size(ToastW, double.PositiveInfinity));
            double h = CardBorder.DesiredSize.Height;
            if (h >= MinH) return (int)Math.Ceiling(h * scale);
        }
        catch { }
        int fallback = string.IsNullOrEmpty(BodyText.Text)
            ? 56
            : (BodyText.Text.Length > 60 ? 92 : 72);
        return (int)(fallback * scale);
    }

    // ── Animation ────────────────────────────────────────────────

    private void BeginFadeIn()
    {
        // Slide via XAML compositor
        var sb = new Storyboard();
        AddAnim(sb, SlideTransform, "X", 20, 0, FadeInMs, new CubicEase { EasingMode = EasingMode.EaseOut });
        sb.Begin();

        // Whole-window alpha via layered-window API
        AnimateAlpha(0, 255, FadeInMs, EaseOutCubic);
    }

    private void BeginFadeOut()
    {
        if (_isFadingOut) return;
        _isFadingOut = true;
        _dismissTimer?.Stop();
        _dismissTimer = null;

        var sb = new Storyboard();
        AddAnim(sb, SlideTransform, "X", 0, 20, FadeOutMs, new QuadraticEase { EasingMode = EasingMode.EaseIn });
        sb.Begin();

        AnimateAlpha(255, 0, FadeOutMs, EaseInQuad, onComplete: () => { try { Close(); } catch { } });
    }

    private void AnimateAlpha(byte from, byte to, int durationMs, Func<double, double> easing, Action? onComplete = null)
    {
        _alphaTimer?.Stop();
        _alphaTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        double elapsed = 0;
        _alphaTimer.Tick += (_, _) =>
        {
            elapsed += 16;
            double t = Math.Min(elapsed / durationMs, 1.0);
            double eased = easing(t);
            byte alpha = (byte)(from + (to - from) * eased);
            SetLayeredWindowAttributes(_hwnd, 0, alpha, LWA_ALPHA);
            if (t >= 1.0)
            {
                _alphaTimer?.Stop();
                _alphaTimer = null;
                onComplete?.Invoke();
            }
        };
        _alphaTimer.Start();
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    private static double EaseInQuad(double t)   => t * t;

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
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
        SetWindowLong(_hwnd, GWL_EXSTYLE, exStyle);

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // Don't let DWM round — XAML's CornerRadius="8" on CardBorder handles
        // corners; DWM rounding plus our layered-alpha window produced a hard
        // black rectangle outside the rounded XAML region.
        int donotround = 1; // DWMWCP_DONOTROUND
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref donotround, sizeof(int));

        uint noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttributeU(_hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(uint));

        // Kill the non-client drop shadow / border rendering — popup shadow
        // composited as opaque under our transparent rounded corners.
        int ncDisabled = 1; // DWMNCRP_DISABLED
        DwmSetWindowAttribute(_hwnd, DWMWA_NCRENDERING_POLICY, ref ncDisabled, sizeof(int));
    }

    private void ApplyOptions(ToastService.ToastOptions opts)
    {
        Color accent = opts.Category switch
        {
            ToastCategory.Screenshot => s_green,
            ToastCategory.Video      => s_green,
            ToastCategory.Clipboard  => s_green,
            ToastCategory.Error      => s_red,
            ToastCategory.Update     => s_blue,
            _                        => s_amber,
        };

        AccentBar.Fill = new SolidColorBrush(accent);

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
    private const int WS_EX_LAYERED    = 0x00080000;

    private const uint LWA_ALPHA = 0x00000002;

    private const int SWP_NOMOVE      = 0x0002;
    private const int SWP_NOSIZE      = 0x0001;
    private const int SWP_NOZORDER    = 0x0004;
    private const int SWP_NOACTIVATE  = 0x0010;
    private const int SWP_FRAMECHANGED = 0x0020;

    private const int DWMWA_NCRENDERING_POLICY      = 2;
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
    [DllImport("user32.dll")] private static extern bool  SetLayeredWindowAttributes(IntPtr h, uint colorKey, byte alpha, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeU(IntPtr h, int attr, ref uint value, int size);
}
