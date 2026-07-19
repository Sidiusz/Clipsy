using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Clipsy.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Point = Windows.Foundation.Point;

namespace Clipsy.Views;

public sealed partial class CaptureOverlayWindow
{
    // ---------- Color picker ----------

    // Cached swatch brush — mutating its Color avoids per-tick allocation.
    private SolidColorBrush? _swatchBrush;
    private Color _colorBeforeFlyout;

    private void OnColorFlyoutOpened(object sender, object e)
    {
        // Snapshot current color so Cancel can revert.
        _colorBeforeFlyout = _drawing.Settings.Color;
        ColorPickerCtl.Color = _colorBeforeFlyout;
        EnsureSwatchBrush().Color = _colorBeforeFlyout;
    }

    private void OnColorPickerChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        // Live preview only (defer Settings.Color until Confirm); mutate the
        // cached brush to avoid the GC churn that caused drag lag.
        var c = Color.FromArgb(0xFF, args.NewColor.R, args.NewColor.G, args.NewColor.B);
        EnsureSwatchBrush().Color = c;
    }

    private void OnColorConfirmClick(object sender, RoutedEventArgs e)
    {
        var c = ColorPickerCtl.Color;
        _drawing.Settings.Color = Color.FromArgb(0xFF, c.R, c.G, c.B);
        ColorFlyout?.Hide();
    }

    private void OnColorCancelClick(object sender, RoutedEventArgs e)
    {
        // Revert swatch to original color; do not touch _drawing.Settings.Color.
        EnsureSwatchBrush().Color = _colorBeforeFlyout;
        ColorPickerCtl.Color = _colorBeforeFlyout;
        ColorFlyout?.Hide();
    }

    private SolidColorBrush EnsureSwatchBrush()
    {
        if (_swatchBrush == null)
        {
            _swatchBrush = new SolidColorBrush(_drawing.Settings.Color);
            ColorSwatch.Fill = _swatchBrush;
        }
        return _swatchBrush;
    }

    // ─── Eyedropper ───

    private bool _eyedropperActive;
    // Pre-copied pixel bytes for fast magnifier rendering (Format32bppArgb BGRA
    // order). The source GDI+ bitmap is dropped after this copy to save ~33 MB.
    private byte[]? _eyedropperPixels;
    private int     _eyedropperStride;
    private int     _eyeW, _eyeH;
    private Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? _magBitmap;

    private void OnEyedropperBtnClick(object sender, RoutedEventArgs e)
    {
        ColorFlyout?.Hide();
        EnsureEyedropperBitmap();
        if (_eyedropperPixels == null) return;

        // WriteableBitmap: 128×128 pixels, rendered at Stretch="Fill" into the 128×128 Grid.
        // No RenderTransform needed — magnified content is written directly into pixels.
        _magBitmap ??= new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(128, 128);
        MagBrush.ImageSource = _magBitmap;

        _eyedropperActive = true;
        EyedropperMagnifier.Visibility = Visibility.Visible;
        RootGrid.Focus(FocusState.Programmatic);

        try
        {
            GetCursorPos(out var pt);
            var scale = DpiScale;
            var dip = new Point((pt.X - _frame.VirtualBounds.X) / scale,
                                (pt.Y - _frame.VirtualBounds.Y) / scale);
            UpdateMagnifier(dip);
        }
        catch { /* no-op */ }
    }

    private void ExitEyedropperMode()
    {
        _eyedropperActive = false;
        EyedropperMagnifier.Visibility = Visibility.Collapsed;
    }

    private void EnsureEyedropperBitmap()
    {
        if (_eyedropperPixels != null) return;
        try
        {
            using var ms = new MemoryStream(_frame.ImageBytes);
            using var bmp = new System.Drawing.Bitmap(ms);
            _eyeW = bmp.Width;
            _eyeH = bmp.Height;

            // Pre-copy all pixels once so sampling/magnifier read from the byte[]
            // (Format32bppArgb = BGRA); the GDI+ bitmap is disposed right after.
            var rect = new System.Drawing.Rectangle(0, 0, _eyeW, _eyeH);
            var data = bmp.LockBits(rect,
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            _eyedropperStride = data.Stride;
            var byteCount = Math.Abs(data.Stride) * _eyeH;
            _eyedropperPixels = new byte[byteCount];
            Marshal.Copy(data.Scan0, _eyedropperPixels, 0, byteCount);
            bmp.UnlockBits(data);
            // CopyFromScreen leaves alpha=0; force opaque so WriteableBitmap renders correctly.
            for (int i = 3; i < _eyedropperPixels.Length; i += 4) _eyedropperPixels[i] = 0xFF;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Eyedropper bitmap decode", ex);
        }
    }

    private void UpdateMagnifier(Point cursorDip)
    {
        // Place magnifier near cursor, offset to avoid covering target pixel.
        double offset = 12;
        double x = cursorDip.X + offset;
        double y = cursorDip.Y + offset;
        double w = RootGrid.Width > 0 ? RootGrid.Width : RootGrid.ActualWidth;
        double h = RootGrid.Height > 0 ? RootGrid.Height : RootGrid.ActualHeight;
        if (x + 128 > w) x = cursorDip.X - 128 - offset;
        if (y + 128 > h) y = cursorDip.Y - 128 - offset;
        if (x < 0) x = 0;
        if (y < 0) y = 0;
        EyedropperMagnifier.Margin = new Thickness(x, y, 0, 0);

        // Render magnified region: srcSize = source pixels fitting the 128px
        // output at ~10× zoom, scaled by DpiScale for consistent zoom.
        if (_eyedropperPixels == null || _magBitmap == null) return;

        const int magPx  = 128;
        int srcSize = Math.Max(1, (int)Math.Round(magPx * DpiScale / 10.0));

        var scale = DpiScale;
        int cx = (int)(cursorDip.X * scale);
        int cy = (int)(cursorDip.Y * scale);
        int srcW   = _eyeW;
        int srcH   = _eyeH;
        int stride = _eyedropperStride;
        int half   = srcSize / 2;

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
                dst[di++] = _eyedropperPixels[si];     // B
                dst[di++] = _eyedropperPixels[si + 1]; // G
                dst[di++] = _eyedropperPixels[si + 2]; // R
                dst[di++] = _eyedropperPixels[si + 3]; // A
            }
        }

        using var stream = _magBitmap.PixelBuffer.AsStream();
        stream.Seek(0, SeekOrigin.Begin);
        stream.Write(dst, 0, dst.Length);
        _magBitmap.Invalidate();

        var c = SamplePixel(cursorDip);
        MagHex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    private Color SamplePixel(Point cursorDip)
    {
        if (_eyedropperPixels == null || _eyeW == 0) return Microsoft.UI.Colors.Black;
        var scale = DpiScale;
        int px = Math.Clamp((int)(cursorDip.X * scale), 0, _eyeW - 1);
        int py = Math.Clamp((int)(cursorDip.Y * scale), 0, _eyeH - 1);
        int i = py * _eyedropperStride + px * 4; // BGRA
        return Color.FromArgb(0xFF, _eyedropperPixels[i + 2], _eyedropperPixels[i + 1], _eyedropperPixels[i]);
    }

    private void ApplyPickedColor(Color c)
    {
        _drawing.Settings.Color = c;
        ColorPickerCtl.Color = c;
        EnsureSwatchBrush().Color = c;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private static Color ParseHexColor(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 8)
        {
            byte a = System.Convert.ToByte(s.Substring(0, 2), 16);
            byte r = System.Convert.ToByte(s.Substring(2, 2), 16);
            byte g = System.Convert.ToByte(s.Substring(4, 2), 16);
            byte b = System.Convert.ToByte(s.Substring(6, 2), 16);
            return Color.FromArgb(a, r, g, b);
        }
        if (s.Length == 6)
        {
            byte r = System.Convert.ToByte(s.Substring(0, 2), 16);
            byte g = System.Convert.ToByte(s.Substring(2, 2), 16);
            byte b = System.Convert.ToByte(s.Substring(4, 2), 16);
            return Color.FromArgb(0xFF, r, g, b);
        }
        return Microsoft.UI.Colors.Red;
    }
}
