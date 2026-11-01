using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
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
    private static readonly Guid CaptureSession3Guid = new("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
    private static readonly Guid CaptureAccessStaticsGuid = new("743ED370-06EC-5040-A58A-901F0F757095");
    private static readonly Guid AsyncInfoGuid = new("00000036-0000-0000-C000-000000000046");
    private static readonly Guid IdxgiDeviceGuid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    private sealed record MonitorTarget(IntPtr Handle, ScreenFreezeService.MonitorInfo Info);
    private sealed record CapturedMonitor(byte[] Bytes, int Width, int Height);

    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) && GraphicsCaptureSession.IsSupported();

    // Device creation costs tens of ms; captures run on one worker thread, so keep it.
    private static IDirect3DDevice? _device;
    private static readonly object _deviceLock = new();

    /// <summary>Pays device creation and JIT at startup so the first PrintScreen isn't slow.</summary>
    public static void Warmup()
    {
        if (!IsSupported) return;
        try { Capture(includeCursor: false); }
        catch (Exception ex) { Diagnostics.Log("WGC warmup failed", ex); }
    }

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

        IDirect3DDevice device;
        lock (_deviceLock) device = _device ??= CreateDirect3DDevice();
        int stride = checked(bounds.Width * 4);
        var pixels = new byte[checked(stride * bounds.Height)];
        var monitors = new List<ScreenFreezeService.MonitorInfo>(targets.Count);

        try
        {
            // In parallel: each monitor waits up to a frame for its first capture.
            var captures = await Task.WhenAll(targets.Select(t => CaptureMonitorAsync(device, t.Handle, includeCursor)));
            for (int i = 0; i < targets.Count; i++)
            {
                CopyMonitor(captures[i], pixels, stride, bounds, targets[i].Info.Bounds);
                monitors.Add(targets[i].Info);
            }
        }
        catch
        {
            // Device may be lost (driver reset); rebuild on the next capture.
            lock (_deviceLock)
                if (ReferenceEquals(_device, device)) _device = null;
            device.Dispose();
            throw;
        }

        ScreenFreezeService.ForceOpaque(pixels);
        return new ScreenFreezeService.FrozenFrame
        {
            PixelBytes = pixels,
            PixelWidth = bounds.Width,
            PixelHeight = bounds.Height,
            VirtualBounds = bounds,
            Monitors = monitors,
        };
    }

    /// <summary>The window's own pixels, even where other windows cover it (minimized windows have none).</summary>
    public static ScreenshotRenderer.RenderedImage CaptureWindow(IntPtr hwnd)
    {
        if (!IsSupported) throw new NotSupportedException("Windows Graphics Capture is not supported.");
        int hr = RoInitialize(RO_INIT_MULTITHREADED);
        bool uninitialize = hr >= 0;
        if (hr < 0 && hr != RPC_E_CHANGED_MODE) Marshal.ThrowExceptionForHR(hr);
        try
        {
            IDirect3DDevice device;
            lock (_deviceLock) device = _device ??= CreateDirect3DDevice();
            var captured = CaptureItemAsync(device, CreateItem(hwnd, window: true), includeCursor: false).GetAwaiter().GetResult();
            ScreenFreezeService.ForceOpaque(captured.Bytes);
            return new ScreenshotRenderer.RenderedImage(captured.Bytes, captured.Width, captured.Height);
        }
        finally { if (uninitialize) RoUninitialize(); }
    }

    private static Task<CapturedMonitor> CaptureMonitorAsync(IDirect3DDevice device, IntPtr hMonitor, bool includeCursor)
        => CaptureItemAsync(device, CreateItem(hMonitor, window: false), includeCursor);

    private static async Task<CapturedMonitor> CaptureItemAsync(
        IDirect3DDevice device, GraphicsCaptureItem item, bool includeCursor)
    {
        var size = item.Size;
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
        using var session = pool.CreateCaptureSession(item);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            try { session.IsCursorCaptureEnabled = includeCursor; } catch { }
        // Otherwise Windows 11 flashes a yellow capture border around the screen.
        DisableBorder(session);

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

    private static int _borderlessRequested;
    private static int _borderFailureLogged;

    // Newer Windows builds ignore IsBorderRequired = false until borderless capture was requested once.
    private static unsafe void RequestBorderlessAccess()
    {
        if (Interlocked.Exchange(ref _borderlessRequested, 1) != 0) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) return;
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureAccess";
        if (WindowsCreateString(cls, cls.Length, out var hstr) < 0) return;
        IntPtr statics = IntPtr.Zero, op = IntPtr.Zero;
        try
        {
            var iid = CaptureAccessStaticsGuid;
            int hr = RoGetActivationFactory(hstr, ref iid, out statics);
            if (hr < 0) { Diagnostics.Log($"GraphicsCaptureAccess unavailable hr=0x{hr:X8}"); return; }
            var request = (delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr*, int>)(*(IntPtr**)statics)[6];
            hr = request(statics, 0 /* Borderless */, &op);
            if (hr < 0 || op == IntPtr.Zero) { Diagnostics.Log($"Borderless capture request failed hr=0x{hr:X8}"); return; }
            // IAsyncOperation<AppCapabilityAccessStatus>: GetResults is slot 8; poll IAsyncInfo.Status (slot 7) first.
            var infoIid = AsyncInfoGuid;
            if (Marshal.QueryInterface(op, ref infoIid, out var info) < 0) return;
            try
            {
                var getStatus = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)(*(IntPtr**)info)[7];
                int status = 0;
                for (int i = 0; i < 200 && getStatus(info, &status) >= 0 && status == 0; i++) Thread.Sleep(10);
                if (status != 1) { Diagnostics.Log($"Borderless capture request ended with async status {status}"); return; }
            }
            finally { Marshal.Release(info); }
            var getResults = (delegate* unmanaged[Stdcall]<IntPtr, int*, int>)(*(IntPtr**)op)[8];
            int access = -1;
            getResults(op, &access);
            if (access != 4) Diagnostics.Log($"Borderless capture not allowed (AppCapabilityAccessStatus {access})");
        }
        catch (Exception ex) { Diagnostics.Log("Borderless capture request failed", ex); }
        finally
        {
            if (op != IntPtr.Zero) Marshal.Release(op);
            if (statics != IntPtr.Zero) Marshal.Release(statics);
            WindowsDeleteString(hstr);
        }
    }

    // IGraphicsCaptureSession3 isn't in the 19041 projection.
    private static unsafe void DisableBorder(GraphicsCaptureSession session)
    {
        RequestBorderlessAccess();
        var iid = CaptureSession3Guid;
        IntPtr unk = MarshalInspectable<GraphicsCaptureSession>.FromManaged(session);
        try
        {
            if (Marshal.QueryInterface(unk, ref iid, out var session3) < 0) return;
            try
            {
                var put = (delegate* unmanaged[Stdcall]<IntPtr, byte, int>)(*(IntPtr**)session3)[7];
                int hr = put(session3, 0);
                if (hr < 0 && Interlocked.Exchange(ref _borderFailureLogged, 1) == 0)
                    Diagnostics.Log($"Hiding the capture border failed hr=0x{hr:X8}");
            }
            finally { Marshal.Release(session3); }
        }
        finally { Marshal.Release(unk); }
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

    private static GraphicsCaptureItem CreateItem(IntPtr handle, bool window)
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
            hr = window
                ? interop.CreateForWindow(handle, ref itemIid, out itemPtr)
                : interop.CreateForMonitor(handle, ref itemIid, out itemPtr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (itemPtr == IntPtr.Zero) throw new InvalidOperationException("Capture item creation returned null.");
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
