using System;
using System.Runtime.InteropServices;
using Clipsy.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private const int FadeInMs    = 220;
    private const int FadeOutMs   = 160;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly Action? _action1;
    private readonly Action? _action2;
    private readonly bool _persistent;
    private readonly int _dismissSeconds;
    private DispatcherTimer? _dismissTimer;
    private EventHandler<object>? _renderHandler;
    private bool _isHovered;
    private bool _fadeInDone;
    private bool _isFadingOut;
    private int _targetX, _targetY, _w, _h, _offscreenX;

    public ToastWindow(ToastService.ToastOptions opts)
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _action1 = opts.Action1Callback;
        _action2 = opts.Action2Callback;
        _persistent = opts.Persistent;
        _dismissSeconds = opts.DismissSeconds;

        ConfigureWindow();
        ApplyOptions(opts);
        ThemeService.Register(Content as FrameworkElement);
        StartDismissTimer();
    }

    // ── Public API ──────────────────────────────────────────────

    internal void PositionAtSlot(int index)
    {
        double scale = DpiScale();
        _w = (int)(ToastW * scale);
        _h = ComputeHeightPx(scale);
        int gap = (int)(ToastGap * scale);
        int margin = (int)(ToastMargin * scale);

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(_hwnd, MONITOR_DEFAULTTOPRIMARY), ref mi);

        _targetX = mi.rcWork.right - _w - margin;
        _targetY = mi.rcWork.bottom - _h - margin - index * (_h + gap);
        _offscreenX = mi.rcWork.right; // window left edge sits exactly at work-area right edge

        if (!_fadeInDone)
        {
            _fadeInDone = true;
            // Show offscreen to the right, then animate left into place.
            _appWindow.MoveAndResize(new RectInt32(_offscreenX, _targetY, _w, _h));
            _appWindow.Show(false);
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            BeginFadeIn();
        }
        else
        {
            // Re-stack snap when another toast closes — no animation.
            SetWindowPos(_hwnd, HWND_TOPMOST, _targetX, _targetY, _w, _h, SWP_NOACTIVATE);
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

    // ── Animation (animates the WINDOW position via Win32) ──────

    private void BeginFadeIn()
    {
        AnimateX(_offscreenX, _targetX, FadeInMs, EaseOutCubic);
    }

    private void BeginFadeOut()
    {
        if (_isFadingOut) return;
        _isFadingOut = true;
        _dismissTimer?.Stop();
        _dismissTimer = null;
        AnimateX(_targetX, _offscreenX, FadeOutMs, EaseInQuad, onComplete: () =>
        {
            try { Close(); } catch { }
        });
    }

    private void AnimateX(int from, int to, int durationMs, Func<double, double> easing, Action? onComplete = null)
    {
        StopRenderHandler();

        var startTime = DateTime.UtcNow;
        const int flags = SWP_NOACTIVATE | SWP_NOSENDCHANGING | SWP_ASYNCWINDOWPOS;

        _renderHandler = (_, _) =>
        {
            double elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            double t = Math.Min(elapsed / durationMs, 1.0);
            double eased = easing(t);
            int x = (int)(from + (to - from) * eased);
            SetWindowPos(_hwnd, HWND_TOPMOST, x, _targetY, _w, _h, flags);
            if (t >= 1.0)
            {
                StopRenderHandler();
                onComplete?.Invoke();
            }
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += _renderHandler;
    }

    private void StopRenderHandler()
    {
        if (_renderHandler != null)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= _renderHandler;
            _renderHandler = null;
        }
    }

    private static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    private static double EaseInQuad(double t)   => t * t;

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
        // Persistent toasts (update prompt) stay until the user acts on them.
        if (_persistent) return;
        _dismissTimer?.Stop();
        _dismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_dismissSeconds > 0 ? _dismissSeconds : 4) };
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

    private const int SWP_NOMOVE         = 0x0002;
    private const int SWP_NOSIZE         = 0x0001;
    private const int SWP_NOZORDER       = 0x0004;
    private const int SWP_NOACTIVATE     = 0x0010;
    private const int SWP_FRAMECHANGED   = 0x0020;
    private const int SWP_NOSENDCHANGING = 0x0400;
    private const int SWP_ASYNCWINDOWPOS = 0x4000;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
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

    [DllImport("user32.dll")] private static extern int    GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern int    SetWindowLong(IntPtr h, int n, int v);
    [DllImport("user32.dll")] private static extern bool   SetWindowPos(IntPtr h, IntPtr z, int x, int y, int cx, int cy, int flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
    [DllImport("user32.dll")] private static extern bool   GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern uint   GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern uint   GetDpiForSystem();

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
}
