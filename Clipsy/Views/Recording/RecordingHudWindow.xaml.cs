using System;
using System.Runtime.InteropServices;
using Clipsy.Localization;
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
    private const string GlyphPause  = "";
    private const string GlyphPlay   = "";
    private const string GlyphLock   = "";
    private const string GlyphUnlock = "";

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _hideTimer;
    private DateTime _startedAt;
    private TimeSpan _accumulated = TimeSpan.Zero;
    private bool _paused;
    private bool _locked = true;

    private bool _draggingMove;
    private Point _moveDragStart;

    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action? StopRequested;
    public event Action? StopSaveRequested;
    public event Action<bool>? LockChanged;
    public event Action<bool>? DrawToggled;
    public event Action<int, int>? MoveDeltaRequested; // dx, dy in screen pixels

    public RecordingHudWindow()
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
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(MoveBtn,     Strings.Get("TipMove"));
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(LockBtn,     Strings.Get("TipLock"));
    }

    public IntPtr Hwnd => _hwnd;

    public void Start()
    {
        _startedAt = DateTime.UtcNow;
        _accumulated = TimeSpan.Zero;
        _paused = false;
        TimerText.Text = "00:00";
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
        int hudW = (int)System.Math.Ceiling(size.Width) + 4;
        int hudH = (int)System.Math.Ceiling(size.Height) + 4;
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
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    private void OnTimerTick(object? s, object e)
    {
        if (_paused) return;
        var elapsed = _accumulated + (DateTime.UtcNow - _startedAt);
        TimerText.Text = elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush _bgClose =
        new(Windows.UI.Color.FromArgb(0xF0, 0x1E, 0x1E, 0x1E));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush _bgFar =
        new(Windows.UI.Color.FromArgb(0x40, 0x1E, 0x1E, 0x1E));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush _borderClose =
        new(Windows.UI.Color.FromArgb(0xFF, 0x2E, 0x2E, 0x2E));
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush _borderFar =
        new(Windows.UI.Color.FromArgb(0x40, 0x2E, 0x2E, 0x2E));

    private bool _hudFar;

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

    private void ApplyHudFar(bool far)
    {
        _hudFar = far;
        // Translucent the panel itself (background + border) but keep the
        // buttons / timer text fully opaque so they stay readable when the
        // cursor is away from the HUD.
        Root.Background  = far ? _bgFar : _bgClose;
        Root.BorderBrush = far ? _borderFar : _borderClose;
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
        LockIcon.Glyph = _locked ? GlyphLock : GlyphUnlock;
        MoveBtn.Visibility = _locked ? Visibility.Collapsed : Visibility.Visible;
        LockChanged?.Invoke(_locked);
        e.Handled = true;
    }

    private void OnDrawToggle(object sender, RoutedEventArgs e)
    {
        DrawToggled?.Invoke(DrawBtn.IsChecked == true);
    }

    // ---------- Move button drag ----------

    private void OnMovePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_locked) return;
        _draggingMove = true;
        if (GetCursorPos(out POINT pt)) _moveDragStart = new Point(pt.X, pt.Y);
        MoveBtn.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMovePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingMove) return;
        if (!GetCursorPos(out POINT pt)) return;
        int dx = (int)(pt.X - _moveDragStart.X);
        int dy = (int)(pt.Y - _moveDragStart.Y);
        if (dx == 0 && dy == 0) return;
        _moveDragStart = new Point(pt.X, pt.Y);
        MoveDeltaRequested?.Invoke(dx, dy);
    }

    private void OnMovePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingMove) return;
        _draggingMove = false;
        MoveBtn.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", SetLastError = true)] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
