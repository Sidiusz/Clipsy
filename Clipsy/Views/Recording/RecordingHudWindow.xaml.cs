using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace Clipsy.Views.Recording;

public sealed partial class RecordingHudWindow : Window
{
    private const string GlyphPause = "";
    private const string GlyphPlay = "";
    private const string GlyphLock = "";
    private const string GlyphUnlock = "";

    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _hideTimer;
    private DateTime _startedAt;
    private TimeSpan _accumulated = TimeSpan.Zero;
    private bool _paused;
    private bool _locked = true;

    public event Action? PauseRequested;
    public event Action? ResumeRequested;
    public event Action? StopRequested;
    public event Action? StopSaveRequested;
    public event Action<bool>? LockChanged;
    public event Action<bool>? DrawToggled;

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
    }

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
        Root.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
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

    private void OnHideTick(object? s, object e)
    {
        if (!GetCursorPos(out POINT pt) || !GetWindowRect(_hwnd, out RECT wr)) return;
        int cx = (wr.Left + wr.Right) / 2;
        int cy = (wr.Top + wr.Bottom) / 2;
        double dist = System.Math.Sqrt((pt.X - cx) * (pt.X - cx) + (pt.Y - cy) * (pt.Y - cy));
        double half = System.Math.Max(wr.Right - wr.Left, wr.Bottom - wr.Top);
        double target = dist < half * 1.6 ? 1.0 : 0.35;
        if (System.Math.Abs(Root.Opacity - target) > 0.02)
        {
            Root.Opacity = target;
        }
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

    private void OnLockToggle(object sender, RoutedEventArgs e)
    {
        _locked = LockBtn.IsChecked == true;
        LockIcon.Glyph = _locked ? GlyphLock : GlyphUnlock;
        LockChanged?.Invoke(_locked);
    }

    private void OnDrawToggle(object sender, RoutedEventArgs e)
    {
        DrawToggled?.Invoke(DrawBtn.IsChecked == true);
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
