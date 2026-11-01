using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Clipsy.Services;

/// <summary>Asks a window to draw itself, for windows that give Windows Graphics Capture no frame.</summary>
internal static class WindowPrintCapture
{
    private const uint PW_RENDERFULLCONTENT = 2;

    /// <summary>The window's pixels cropped to <paramref name="bounds"/> (physical screen pixels); null when it has none.</summary>
    public static ScreenshotRenderer.RenderedImage? Capture(IntPtr hwnd, Rectangle bounds)
    {
        // PrintWindow waits on the window's thread, so a hung app would block the capture.
        if (IsIconic(hwnd) || IsHungAppWindow(hwnd) || !GetWindowRect(hwnd, out var r)) return null;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        var crop = Rectangle.Intersect(new Rectangle(bounds.X - r.Left, bounds.Y - r.Top, bounds.Width, bounds.Height),
            new Rectangle(0, 0, width, height));
        if (crop.Width <= 0 || crop.Height <= 0) return null;

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            var hdc = g.GetHdc();
            try { if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)) return null; }
            finally { g.ReleaseHdc(hdc); }
        }

        var data = bitmap.LockBits(crop, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = crop.Width * 4;
            var pixels = new byte[rowBytes * crop.Height];
            for (int y = 0; y < crop.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * rowBytes, rowBytes);
            ScreenFreezeService.ForceOpaque(pixels);
            return IsBlank(pixels) ? null : new ScreenshotRenderer.RenderedImage(pixels, crop.Width, crop.Height);
        }
        finally { bitmap.UnlockBits(data); }
    }

    // A window that can't print itself succeeds with an empty black image.
    internal static bool IsBlank(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
            if (bgra[i] != 0 || bgra[i + 1] != 0 || bgra[i + 2] != 0) return false;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsHungAppWindow(IntPtr hwnd);
}
