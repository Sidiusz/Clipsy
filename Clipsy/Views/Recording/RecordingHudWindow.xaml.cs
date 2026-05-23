using System;
using System.Runtime.InteropServices;
using Clipsy.Localization;
using Clipsy.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics;
using WinRT.Interop;

namespace Clipsy.Views.Recording;

public sealed partial class RecordingHudWindow : Window
{
    private const string GlyphPause  = "\uE769";
    private const string GlyphPlay   = "\uE768";
    private const string GlyphLock   = "\uE72E";
    private const string GlyphUnlock = "\uE785";

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _hideTimer;
    private DateTime _startedAt;
    private TimeSpan _accumulated = TimeSpan.Zero;
    private bool _paused;
    private bool _locked = true;

    private const bool _draggingMove = false;

    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action? StopRequested;
    public event Action? StopSaveRequested;
    public event Action<bool>? LockChanged;
    public event Action<bool>? DrawToggled;

    public RecordingHudWindow()
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
        SetWindowExStyle();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += OnTimerTick;
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hideTimer.Tick += OnHideTick;
        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(PauseBtn,    Strings.Get("TipPause"));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(StopBtn,     Strings.Get("TipStop"));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(StopSaveBtn, Strings.Get("TipSaveAs"));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(DrawBtn,     Strings.Get("TipDraw"));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(LockBtn,     Strings.Get("TipLock"));
    }

    public IntPtr Hwnd => _hwnd;

    public void Start()
    {
        _startedAt = DateTime.UtcNow;
        _accumulated = TimeSpan.Zero;
        _paused = false;
        TimerText.Text = "00:00";
        PauseIcon.Glyph = GlyphPause;
        ApplyLockVisual();
        Root.Opacity = 1.0;
        _timer.Start();
        _hideTimer.Start();
    }

    public void Shutdown()
    {
        _timer.Stop();
        _hideTimer.Stop();
    }

    public void PositionBelowRegion(int regionX, int regionY, int regionW, int regionH, int virtualScreenH)
    {
        Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = Root.DesiredSize;
        int hudW = (int)System.Math.Ceiling(size.Width);
        int hudH = (int)System.Math.Ceiling(size.Height);
        int hudX = regionX + (regionW - hudW) / 2;
        int hudY = regionY + regionH + 8;
        if (hudY + hudH > virtualScreenH - 8)
        {
            hudY = regionY - hudH - 8;
        }
        _appWindow.MoveAndResize(new RectInt32(hudX, hudY, hudW, hudH));
    }

    private void SetWindowExStyle()
    {
        var ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);

        // Strip resizable frame + caption that WinUI window inherits. Without
        // this, Win11 paints a 1-2px chrome border around the HUD even after
        // SetBorderAndTitleBar(false, false).
        var style = (uint)GetWindowLong(_hwnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        style |= WS_POPUP;
        SetWindowLong(_hwnd, GWL_STYLE, unchecked((int)style));
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // Belt + suspenders: also tell DWM to skip the border color.
        uint borderColor = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(uint));
    }

    private const int GWL_STYLE = -16;
    private const uint WS_POPUP      = 0x80000000;
    private const uint WS_CAPTION    = 0x00C00000;
    private const uint WS_THICKFRAME = 0x00040000;
    private const uint WS_MINIMIZEBOX = 0x00020000;
    private const uint WS_MAXIMIZEBOX = 0x00010000;
    private const uint WS_SYSMENU    = 0x00080000;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int DWMWA_BORDER_COLOR = 34;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref uint value, int size);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private void OnTimerTick(object? s, object e)
    {
        if (_paused) return;
        var elapsed = _accumulated + (DateTime.UtcNow - _startedAt);
        TimerText.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    private void OnHideTick(object? s, object e)
    {
        if (_draggingMove) { ApplyHudFar(false); return; }
        if (!GetCursorPos(out POINT pt) || !GetWindowRect(_hwnd, out RECT wr)) return;
        int cx = (wr.Left + wr.Right) / 2;
        int cy = (wr.Top + wr.Bottom) / 2;
        double dist = System.Math.Sqrt((pt.X - cx) * (pt.X - cx) + (pt.Y - cy) * (pt.Y - cy));
        double half = System.Math.Max(wr.Right - wr.Left, wr.Bottom - wr.Top);
        bool far = dist >= half * 1.3;
        if (far != _hudFar) ApplyHudFar(far);
    }

    private bool _hudFar;

    private void ApplyHudFar(bool far)
    {
        _hudFar = far;
        Root.Opacity = far ? 0.60 : 1.0;
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        _paused = !_paused;
        if (_paused)
        {
            _accumulated += DateTime.UtcNow - _startedAt;
            PauseIcon.Glyph = GlyphPlay;
            PauseRequested?.Invoke();
        }
        else
        {
            _startedAt = DateTime.UtcNow;
            PauseIcon.Glyph = GlyphPause;
            ResumeRequested?.Invoke();
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void OnStopSaveClick(object sender, RoutedEventArgs e) => StopSaveRequested?.Invoke();

    private void OnLockDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _locked = !_locked;
        ApplyLockVisual();
        LockChanged?.Invoke(_locked);
        e.Handled = true;
    }

    private void OnDrawToggle(object sender, RoutedEventArgs e)
    {
        DrawToggled?.Invoke(DrawBtn.IsChecked == true);
    }

    private void ApplyLockVisual()
    {
        LockIcon.Glyph = _locked ? GlyphLock : GlyphUnlock;
        // When the region is unlocked, switch the lock button to the accent
        // "active" style so the user sees at a glance that resize/move is on.
        var style = (Microsoft.UI.Xaml.Style)Application.Current.Resources[
            _locked ? "ClipsyIconButton" : "ClipsyIconButtonActive"];
        LockBtn.Style = style;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int LWA_ALPHA = 0x00000002;

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, int dwFlags);
}
