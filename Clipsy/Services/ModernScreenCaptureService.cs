using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Clipsy.Services;

internal static class ModernScreenCaptureService
{
    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    private sealed record MonitorTarget(IntPtr Handle, ScreenFreezeService.MonitorInfo Info);
    private sealed record CapturedMonitor(byte[] Bytes, int Width, int Height);

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(6, 2);

    public static ScreenFreezeService.FrozenFrame Capture(bool includeCursor)
    {
        if (!IsSupported) throw new NotSupportedException("DXGI Desktop Duplication is not supported.");
        var bounds = ScreenFreezeService.GetVirtualScreenBounds();
        var targets = EnumerateTargets();
        if (bounds.Width <= 0 || bounds.Height <= 0 || targets.Count == 0)
            throw new InvalidOperationException("No monitors are available for capture.");

        int stride = checked(bounds.Width * 4);
        var pixels = new byte[checked(stride * bounds.Height)];
        var monitors = new List<ScreenFreezeService.MonitorInfo>(targets.Count);

        foreach (var target in targets)
        {
            var captured = CaptureMonitor(target.Info.Bounds);
            CopyMonitor(captured, pixels, stride, bounds, target.Info.Bounds);
            monitors.Add(target.Info);
        }

        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 0xFF;

        if (includeCursor)
            DrawCursor(pixels, bounds);

        return new ScreenFreezeService.FrozenFrame
        {
            PixelBytes = pixels,
            PixelWidth = bounds.Width,
            PixelHeight = bounds.Height,
            VirtualBounds = bounds,
            Monitors = monitors,
        };
    }

    private static CapturedMonitor CaptureMonitor(Rectangle monitorBounds)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint adapterIndex = 0;
             factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Success;
             adapterIndex++)
        {
            using (adapter)
            {
                if (adapter == null || (adapter.Description1.Flags & AdapterFlags.Software) != 0)
                    continue;

                for (uint outputIndex = 0;
                     adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Success;
                     outputIndex++)
                {
                    using (output)
                    {
                        if (output == null) continue;
                        var desc = output.Description;
                        var desktop = desc.DesktopCoordinates;
                        bool sameBounds = desktop.Left == monitorBounds.Left &&
                            desktop.Top == monitorBounds.Top &&
                            desktop.Right == monitorBounds.Right &&
                            desktop.Bottom == monitorBounds.Bottom;
                        if (!desc.AttachedToDesktop || !sameBounds)
                            continue;

                        using var output1 = output.QueryInterface<IDXGIOutput1>();
                        return CaptureOutput(adapter, output1, monitorBounds.Width, monitorBounds.Height);
                    }
                }
            }
        }

        throw new InvalidOperationException($"No DXGI output matched monitor {monitorBounds}.");
    }

    private static CapturedMonitor CaptureOutput(
        IDXGIAdapter1 adapter, IDXGIOutput1 output, int width, int height)
    {
        D3D11.D3D11CreateDevice(
            adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            FeatureLevels, out ID3D11Device? device).CheckError();
        if (device == null) throw new InvalidOperationException("D3D11 device creation returned null.");
        using (device)
        using (var duplication = output.DuplicateOutput(device))
        {
            var textureDesc = new Texture2DDescription
            {
                CPUAccessFlags = CpuAccessFlags.Read,
                BindFlags = BindFlags.None,
                Format = Format.B8G8R8A8_UNorm,
                Width = (uint)width,
                Height = (uint)height,
                MiscFlags = ResourceOptionFlags.None,
                MipLevels = 1,
                ArraySize = 1,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
            };

            using var staging = device.CreateTexture2D(textureDesc);
            return ReadNextFrame(device, duplication, staging, width, height);
        }
    }

    private static CapturedMonitor ReadNextFrame(
        ID3D11Device device, IDXGIOutputDuplication duplication,
        ID3D11Texture2D staging, int width, int height)
    {
        IDXGIResource? desktopResource = null;
        bool acquired = false;
        try
        {
            var result = duplication.AcquireNextFrame(750, out _, out desktopResource);
            result.CheckError();
            acquired = true;
            if (desktopResource == null)
                throw new InvalidOperationException("Desktop Duplication returned no resource.");

            using var texture = desktopResource.QueryInterface<ID3D11Texture2D>();
            device.ImmediateContext.CopyResource(staging, texture);
            var mapped = device.ImmediateContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                int rowBytes = checked(width * 4);
                var bytes = new byte[checked(rowBytes * height)];
                for (int y = 0; y < height; y++)
                {
                    var src = new IntPtr(mapped.DataPointer + (long)y * mapped.RowPitch);
                    Marshal.Copy(src, bytes, y * rowBytes, rowBytes);
                }
                return new CapturedMonitor(bytes, width, height);
            }
            finally { device.ImmediateContext.Unmap(staging, 0); }
        }
        finally
        {
            desktopResource?.Dispose();
            if (acquired) duplication.ReleaseFrame();
        }
    }

    private static void CopyMonitor(CapturedMonitor src, byte[] dst, int dstStride,
        Rectangle virtualBounds, Rectangle monitorBounds)
    {
        int copyW = Math.Min(src.Width, monitorBounds.Width);
        int copyH = Math.Min(src.Height, monitorBounds.Height);
        if (copyW <= 0 || copyH <= 0) return;

        int dstX = monitorBounds.X - virtualBounds.X;
        int dstY = monitorBounds.Y - virtualBounds.Y;
        int srcStride = src.Width * 4;
        int rowBytes = copyW * 4;
        for (int y = 0; y < copyH; y++)
        {
            int dstOffset = (dstY + y) * dstStride + dstX * 4;
            Buffer.BlockCopy(src.Bytes, y * srcStride, dst, dstOffset, rowBytes);
        }
    }

    private static List<MonitorTarget> EnumerateTargets()
    {
        var list = new List<MonitorTarget>();
        int index = 0;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(hMon, ref mi)) return true;
            var bounds = Rectangle.FromLTRB(
                mi.rcMonitor.left, mi.rcMonitor.top, mi.rcMonitor.right, mi.rcMonitor.bottom);
            list.Add(new MonitorTarget(hMon,
                new ScreenFreezeService.MonitorInfo(index++, bounds, (mi.dwFlags & 1) != 0)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static void DrawCursor(byte[] pixels, Rectangle bounds)
    {
        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        var area = new Rectangle(0, 0, bounds.Width, bounds.Height);
        var data = bmp.LockBits(area, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(pixels, 0, data.Scan0, pixels.Length); }
        finally { bmp.UnlockBits(data); }

        using (var g = Graphics.FromImage(bmp))
            DrawCursorOnto(g, bounds.X, bounds.Y);

        data = bmp.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
        finally { bmp.UnlockBits(data); }
    }

    private static void DrawCursorOnto(Graphics g, int originX, int originY)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0) return;
        var hdc = g.GetHdc();
        try
        {
            DrawIconEx(hdc,
                ci.ptScreenPos.X - originX,
                ci.ptScreenPos.Y - originY,
                ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally { g.ReleaseHdc(hdc); }
    }

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

    private const int CURSOR_SHOWING = 0x0001;
    private const uint DI_NORMAL = 0x0003;

    private delegate bool MonitorEnumProc(
        IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(
        IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy,
        uint step, IntPtr brush, uint flags);
}
