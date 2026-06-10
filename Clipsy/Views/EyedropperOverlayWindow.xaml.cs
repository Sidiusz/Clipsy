using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Clipsy.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Clipsy.Views;

/// <summary>
/// Fullscreen transparent WinUI overlay for screen-colour picking.
/// Shared component — used by <see cref="CaptureOverlayWindow"/> (pass the
/// existing frozen frame) and by <see cref="Recording.HudColorPickerWindow"/>
/// (pass null; the window captures a fresh screenshot itself).
/// Call the static <see cref="Open"/> factory from the UI thread.
/// </summary>
public sealed partial class EyedropperOverlayWindow : Window
{
    // ── Injected ──────────────────────────────────────────────────────
    private readonly ScreenFreezeService.FrozenFrame   _frame;
    private readonly Action<Color> _onPicked;
    private readonly Action?       _onCanceled;

    // ── Decoded bitmap + pixel cache ──────────────────────────────────
    private System.Drawing.Bitmap? _bitmap;
    private byte[]?                _pixels;
    private int                    _stride;

    // ── Magnifier (identical logic to CaptureOverlayWindow) ───────────
    private WriteableBitmap? _magBitmap;
    private const double MagSize = 128;
    private const double MagGap  = 12;
    private Point  _magCursor;
    private bool   _magSideLeft, _magSideTop;
    private double _magOffX, _magOffY;
    private double _magTgtOffX, _magTgtOffY;
    private bool   _magShown, _magTweening;

    // ── Win32 ─────────────────────────────────────────────────────────
    private readonly IntPtr    _hwnd;
    private readonly AppWindow _appWindow;
    private bool               _closed;
    private DispatcherQueue?   _dispatcherQueue;

    // ──────────────────────────────────────────────────────────────────
    // Factory
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Open the eyedropper overlay. If <paramref name="frame"/> is null a
    /// fresh screenshot is captured (recording context). Call from UI thread.
    /// Left-click picks colour and fires <paramref name="onPicked"/>.
    /// Right-click / deactivate fires <paramref name="onCanceled"/>.
    /// </summary>
    public static void Open(
        ScreenFreezeService.FrozenFrame? frame,
        Action<Color> onPicked,
        Action? onCanceled = null)
    {
        var f = frame ?? new ScreenFreezeService().Capture();
        var w = new EyedropperOverlayWindow(f, onPicked, onCanceled);
        w.Activate();
    }

    // ──────────────────────────────────────────────────────────────────
    // Constructor
    // ──────────────────────────────────────────────────────────────────

    private EyedropperOverlayWindow(ScreenFreezeService.FrozenFrame frame, Action<Color> onPicked, Action? onCanceled)
    {
        _frame      = frame;
        _onPicked   = onPicked;
        _onCanceled = onCanceled;

        InitializeComponent();

        _hwnd      = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        ConfigureWindow();
        DecodeFrame();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        SetupFrameImage();

        Activated += OnActivated;
        Closed    += (_, _) => FreeBitmap();
    }

    // ──────────────────────────────────────────────────────────────────
    // Pointer handlers
    // ──────────────────────────────────────────────────────────────────

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pixels == null) return;
        var pos = e.GetCurrentPoint(RootGrid).Position;

        // WriteableBitmap needs XamlRoot — lazy-init after first layout.
        if (_magBitmap == null)
        {
            _magBitmap = new WriteableBitmap(128, 128);
            MagBrush.ImageSource = _magBitmap;
            EyedropperMagnifier.Visibility = Visibility.Visible;
        }

        UpdateMagnifier(pos);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_closed) return;
        var cp  = e.GetCurrentPoint(RootGrid);
        bool lmb = cp.Properties.IsLeftButtonPressed;
        Color? picked = lmb && _pixels != null ? SamplePixel(cp.Position) : null;

        DoClose();

        if (picked.HasValue) _dispatcherQueue?.TryEnqueue(() => _onPicked(picked.Value));
        else                 _dispatcherQueue?.TryEnqueue(() => _onCanceled?.Invoke());

        e.Handled = true;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated && !_closed)
        {
            DoClose();
            _dispatcherQueue?.TryEnqueue(() => _onCanceled?.Invoke());
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Magnifier — identical logic extracted from CaptureOverlayWindow
    // ──────────────────────────────────────────────────────────────────

    private void UpdateMagnifier(Point cursorDip)
    {
        _magCursor = cursorDip;
        UpdateMagnifierSide();
        ApplyMagnifierPos();

        if (_pixels == null || _magBitmap == null || _bitmap == null) return;

        const int magPx = 128;
        double scale = DpiScale;
        int srcSize = Math.Max(1, (int)Math.Round(magPx * scale / 10.0));
        int cx = (int)(cursorDip.X * scale);
        int cy = (int)(cursorDip.Y * scale);
        int srcW = _bitmap.Width, srcH = _bitmap.Height, stride = _stride;
        int half = srcSize / 2;

        var dst = new byte[magPx * magPx * 4];
        int di = 0;
        for (int dy = 0; dy < magPx; dy++)
        {
            int srcY = Math.Clamp(cy - half + (int)((double)dy / magPx * srcSize), 0, srcH - 1);
            int rowBase = srcY * stride;
            for (int dx = 0; dx < magPx; dx++)
            {
                int srcX = Math.Clamp(cx - half + (int)((double)dx / magPx * srcSize), 0, srcW - 1);
                int si = rowBase + srcX * 4;
                dst[di++] = _pixels[si];     // B
                dst[di++] = _pixels[si + 1]; // G
                dst[di++] = _pixels[si + 2]; // R
                dst[di++] = _pixels[si + 3]; // A
            }
        }

        using var stream = _magBitmap.PixelBuffer.AsStream();
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(dst, 0, dst.Length);
        _magBitmap.Invalidate();
    }

    private void UpdateMagnifierSide()
    {
        double w = RootGrid.ActualWidth, h = RootGrid.ActualHeight;

        if (!_magSideLeft) { if (_magCursor.X + MagGap + MagSize > w) _magSideLeft = true; }
        else if (_magCursor.X - MagSize - MagGap < 0) _magSideLeft = false;

        if (!_magSideTop) { if (_magCursor.Y + MagGap + MagSize > h) _magSideTop = true; }
        else if (_magCursor.Y - MagSize - MagGap < 0) _magSideTop = false;

        _magTgtOffX = _magSideLeft ? -(MagSize + MagGap) : MagGap;
        _magTgtOffY = _magSideTop  ? -(MagSize + MagGap) : MagGap;

        if (!_magShown) { _magOffX = _magTgtOffX; _magOffY = _magTgtOffY; _magShown = true; return; }
        if (_magOffX != _magTgtOffX || _magOffY != _magTgtOffY) StartMagTween();
    }

    private void ApplyMagnifierPos()
    {
        double w = RootGrid.ActualWidth, h = RootGrid.ActualHeight;
        MagTransform.X = Math.Clamp(_magCursor.X + _magOffX, 0, Math.Max(0, w - MagSize));
        MagTransform.Y = Math.Clamp(_magCursor.Y + _magOffY, 0, Math.Max(0, h - MagSize));
    }

    private void StartMagTween()
    {
        if (_magTweening) return;
        _magTweening = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnMagTween;
    }

    private void StopMagTween()
    {
        if (!_magTweening) return;
        _magTweening = false;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnMagTween;
    }

    private void OnMagTween(object? sender, object e)
    {
        const double smooth = 0.22;
        _magOffX += (_magTgtOffX - _magOffX) * smooth;
        _magOffY += (_magTgtOffY - _magOffY) * smooth;
        if (Math.Abs(_magTgtOffX - _magOffX) < 0.5 && Math.Abs(_magTgtOffY - _magOffY) < 0.5)
        {
            _magOffX = _magTgtOffX;
            _magOffY = _magTgtOffY;
            StopMagTween();
        }
        ApplyMagnifierPos();
    }

    private Color SamplePixel(Point dipPos)
    {
        if (_bitmap == null) return Microsoft.UI.Colors.Black;
        double scale = DpiScale;
        int px = Math.Clamp((int)(dipPos.X * scale), 0, _bitmap.Width  - 1);
        int py = Math.Clamp((int)(dipPos.Y * scale), 0, _bitmap.Height - 1);
        var c = _bitmap.GetPixel(px, py);
        return Color.FromArgb(0xFF, c.R, c.G, c.B);
    }

    private double DpiScale => Content?.XamlRoot?.RasterizationScale ?? (GetDpiForWindow(_hwnd) / 96.0);

    // ──────────────────────────────────────────────────────────────────
    // Window setup
    // ──────────────────────────────────────────────────────────────────

    private void ConfigureWindow()
    {
        var b = _frame.VirtualBounds;

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
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        // Exclude from screen recording so the loupe doesn't appear in the video.
        SetWindowDisplayAffinity(_hwnd, WDA_EXCLUDEFROMCAPTURE);

        // Position over the full virtual screen (physical pixel coordinates).
        _appWindow.MoveAndResize(new RectInt32(b.X, b.Y, b.Width, b.Height));
    }

    private void SetupFrameImage()
    {
        if (_bitmap == null || _pixels == null) return;
        var wb = new WriteableBitmap(_bitmap.Width, _bitmap.Height);
        using var stream = wb.PixelBuffer.AsStream();
        stream.Write(_pixels, 0, _pixels.Length);
        wb.Invalidate();
        FrameImage.Source = wb;
    }

    private void DecodeFrame()
    {
        try
        {
            using var ms = new MemoryStream(_frame.ImageBytes);
            _bitmap = new System.Drawing.Bitmap(ms);
            var rect = new System.Drawing.Rectangle(0, 0, _bitmap.Width, _bitmap.Height);
            var data = _bitmap.LockBits(rect,
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            _stride = data.Stride;
            _pixels = new byte[Math.Abs(data.Stride) * _bitmap.Height];
            Marshal.Copy(data.Scan0, _pixels, 0, _pixels.Length);
            _bitmap.UnlockBits(data);
            // CopyFromScreen leaves alpha=0; force opaque so WriteableBitmap renders correctly.
            for (int i = 3; i < _pixels.Length; i += 4) _pixels[i] = 0xFF;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("EyedropperOverlayWindow.DecodeFrame", ex);
        }
    }

    private void FreeBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _pixels = null;
    }

    private void DoClose()
    {
        if (_closed) return;
        _closed = true;
        StopMagTween();
        try { _appWindow.Hide(); } catch { }
        try { Close(); } catch { }
    }

    // ──────────────────────────────────────────────────────────────────
    // Win32
    // ──────────────────────────────────────────────────────────────────

    private const int  GWL_STYLE       = -16;
    private const int  GWL_EXSTYLE     = -20;
    private const uint WS_POPUP        = 0x80000000;
    private const uint WS_CAPTION      = 0x00C00000;
    private const uint WS_THICKFRAME   = 0x00040000;
    private const uint WS_MINIMIZEBOX  = 0x00020000;
    private const uint WS_MAXIMIZEBOX  = 0x00010000;
    private const uint WS_SYSMENU      = 0x00080000;
    private const int  WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOMOVE      = 0x0002;
    private const uint SWP_NOSIZE      = 0x0001;
    private const uint SWP_NOZORDER    = 0x0004;
    private const uint SWP_NOACTIVATE  = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll")] private static extern int  GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern int  SetWindowLong(IntPtr h, int n, int v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
}
