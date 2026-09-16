using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using WinRT;

namespace Clipsy.Services;

internal static class ModernScreenCaptureService
{
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid CaptureInteropGuid = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IdxgiDeviceGuid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    private sealed record MonitorTarget(IntPtr Handle, ScreenFreezeService.MonitorInfo Info);
    private sealed record CapturedMonitor(byte[] Bytes, int Width, int Height);

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && GraphicsCaptureSession.IsSupported();

    public static ScreenFreezeService.FrozenFrame Capture(bool includeCursor)
    {
        if (!IsSupported) throw new NotSupportedException("Windows Graphics Capture is not supported.");
        int hr = RoInitialize(RO_INIT_MULTITHREADED);
        bool uninitialize = hr >= 0;
        if (hr < 0 && hr != RPC_E_CHANGED_MODE) Marshal.ThrowExceptionForHR(hr);
        try { return CaptureAsync(includeCursor).GetAwaiter().GetResult(); }
        finally { if (uninitialize) RoUninitialize(); }
    }

    private static async Task<ScreenFreezeService.FrozenFrame> CaptureAsync(bool includeCursor)
    {
        var bounds = ScreenFreezeService.GetVirtualScreenBounds();
        var targets = EnumerateTargets();
        if (bounds.Width <= 0 || bounds.Height <= 0 || targets.Count == 0)
            throw new InvalidOperationException("No monitors are available for capture.");

        using var device = CreateDirect3DDevice();
        int stride = checked(bounds.Width * 4);
        var pixels = new byte[checked(stride * bounds.Height)];
        var monitors = new List<ScreenFreezeService.MonitorInfo>(targets.Count);

        foreach (var target in targets)
        {
            var captured = await CaptureMonitorAsync(device, target.Handle, includeCursor);
            CopyMonitor(captured, pixels, stride, bounds, target.Info.Bounds);
            monitors.Add(target.Info);
        }

        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;
        return new ScreenFreezeService.FrozenFrame
        {
            PixelBytes = pixels,
            PixelWidth = bounds.Width,
            PixelHeight = bounds.Height,
            VirtualBounds = bounds,
            Monitors = monitors,
        };
    }

    private static async Task<CapturedMonitor> CaptureMonitorAsync(
        IDirect3DDevice device, IntPtr hMonitor, bool includeCursor)
    {
        var item = CreateItemForMonitor(hMonitor);
        var size = item.Size;
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
        using var session = pool.CreateCaptureSession(item);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            try { session.IsCursorCaptureEnabled = includeCursor; } catch { }

        var tcs = new TaskCompletionSource<Direct3D11CaptureFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Windows.Foundation.TypedEventHandler<Direct3D11CaptureFramePool, object> handler = (sender, _) =>
        {
            try
            {
                var frame = sender.TryGetNextFrame();
                if (frame != null && !tcs.TrySetResult(frame)) frame.Dispose();
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        };

        pool.FrameArrived += handler;
        try
        {
            session.StartCapture();
            using var frame = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                frame.Surface, BitmapAlphaMode.Ignore);
            return CopySoftwareBitmap(bitmap);
        }
        finally { pool.FrameArrived -= handler; }
    }

    private static CapturedMonitor CopySoftwareBitmap(SoftwareBitmap source)
    {
        SoftwareBitmap bitmap = source;
        bool disposeConverted = false;
        if (source.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
            source.BitmapAlphaMode != BitmapAlphaMode.Ignore)
        {
            bitmap = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            disposeConverted = true;
        }

        try
        {
            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            var buffer = new Windows.Storage.Streams.Buffer((uint)checked(width * height * 4));
            bitmap.CopyToBuffer(buffer);
            var bytes = new byte[buffer.Length];
            using var stream = buffer.AsStream();
            stream.ReadExactly(bytes);
            return new CapturedMonitor(bytes, width, height);
        }
        finally
        {
            if (disposeConverted) bitmap.Dispose();
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
            var info = new ScreenFreezeService.MonitorInfo(index++, bounds, (mi.dwFlags & 1) != 0);
            list.Add(new MonitorTarget(hMon, info));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr hMonitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        int hr = WindowsCreateString(className, className.Length, out var hstring);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        IntPtr factoryPtr = IntPtr.Zero;
        IntPtr itemPtr = IntPtr.Zero;
        try
        {
            var factoryIid = CaptureInteropGuid;
            hr = RoGetActivationFactory(hstring, ref factoryIid, out factoryPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
            var itemIid = GraphicsCaptureItemGuid;
            hr = interop.CreateForMonitor(hMonitor, ref itemIid, out itemPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (itemPtr == IntPtr.Zero) throw new InvalidOperationException("CreateForMonitor returned null.");
            return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
        }
        finally
        {
            if (itemPtr != IntPtr.Zero) Marshal.Release(itemPtr);
            if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
            WindowsDeleteString(hstring);
        }
    }

    private static IDirect3DDevice CreateDirect3DDevice()
    {
        int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION,
            out var d3dDevice, out _, out var context);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        IntPtr dxgiDevice = IntPtr.Zero;
        IntPtr graphicsDevice = IntPtr.Zero;
        try
        {
            var iid = IdxgiDeviceGuid;
            hr = Marshal.QueryInterface(d3dDevice, ref iid, out dxgiDevice);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out graphicsDevice);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevice);
        }
        finally
        {
            if (graphicsDevice != IntPtr.Zero) Marshal.Release(graphicsDevice);
            if (dxgiDevice != IntPtr.Zero) Marshal.Release(dxgiDevice);
            if (context != IntPtr.Zero) Marshal.Release(context);
            if (d3dDevice != IntPtr.Zero) Marshal.Release(d3dDevice);
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
        [PreserveSig] int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    private const uint RO_INIT_MULTITHREADED = 1;
    private const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);
    private const int D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
    private const uint D3D11_SDK_VERSION = 7;

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

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor,
        IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip,
        MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("combase.dll")]
    private static extern int RoInitialize(uint initType);

    [DllImport("combase.dll")]
    private static extern void RoUninitialize();

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelsCount, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(
        IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
