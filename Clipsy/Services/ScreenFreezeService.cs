using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Clipsy.Services;

/// <summary>Captures a still of the virtual screen (all monitors) plus
/// per-monitor geometry as a raw image buffer for the XAML pipeline.</summary>
public sealed class ScreenFreezeService
{
    public sealed record MonitorInfo(int Index, Rectangle Bounds, bool IsPrimary);

    public sealed class FrozenFrame
    {
        // Raw top-down BGRA32, opaque, tightly packed (stride = Width*4). Kept
        // decoded: BMP encode + re-decode churned ~100 MB on the LOH per capture
        // and stalled the open on GC. This copies straight into WriteableBitmap.
        public required byte[] PixelBytes { get; init; }
        public required int PixelWidth { get; init; }
        public required int PixelHeight { get; init; }
        public required Rectangle VirtualBounds { get; init; }
        public required IReadOnlyList<MonitorInfo> Monitors { get; init; }
    }

    public FrozenFrame Capture()
    {
        var bounds = GetVirtualScreenBounds();
        var monitors = EnumerateMonitors();

        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.None;

            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);

            if (SettingsService.Instance.Settings.CaptureScreenshotCursor)
                DrawCursorOnto(g, bounds.X, bounds.Y);
        }

        int w = bmp.Width, h = bmp.Height;
        int stride = w * 4;
        var pixels = new byte[stride * h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride == stride)
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            else
                for (int y = 0; y < h; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * stride, stride);
        }
        finally { bmp.UnlockBits(data); }

        // CopyFromScreen leaves alpha at 0; force opaque so the premultiplied
        // WriteableBitmap shows the frame instead of full transparency.
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;

        return new FrozenFrame
        {
            PixelBytes = pixels,
            PixelWidth = w,
            PixelHeight = h,
            VirtualBounds = bounds,
            Monitors = monitors,
        };
    }

    // Rebuilds a GDI bitmap from the raw buffer for the eyedropper/renderer
    // (off the launch hot path). Caller owns the returned bitmap.
    public static Bitmap CreateBitmap(FrozenFrame f)
    {
        var bmp = new Bitmap(f.PixelWidth, f.PixelHeight, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, f.PixelWidth, f.PixelHeight),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(f.PixelBytes, 0, data.Scan0, f.PixelBytes.Length); }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }

    public static Rectangle GetVirtualScreenBounds()
    {
        int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
        int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
        int h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        return new Rectangle(x, y, w, h);
    }

    private static IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();
        int idx = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                var rect = new Rectangle(
                    mi.rcMonitor.left,
                    mi.rcMonitor.top,
                    mi.rcMonitor.right - mi.rcMonitor.left,
                    mi.rcMonitor.bottom - mi.rcMonitor.top);
                list.Add(new MonitorInfo(idx++, rect, (mi.dwFlags & 1) == 1));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private static void DrawCursorOnto(Graphics g, int originX, int originY)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0)
            return;
        var hdc = g.GetHdc();
        try
        {
            DrawIconEx(hdc,
                ci.ptScreenPos.X - originX,
                ci.ptScreenPos.Y - originY,
                ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
    }

    private const int CURSOR_SHOWING = 0x0001;
    private const uint DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORPOINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public CURSORPOINT ptScreenPos;
    }

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y,
        IntPtr hIcon, int cx, int cy, uint step, IntPtr brush, uint flags);
}
