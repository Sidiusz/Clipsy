using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <summary>Win32 clipboard writes with every format rendered up front: safe on any thread, and apps
/// reading the clipboard never call back into Clipsy (the OLE path blocked the UI thread under load).</summary>
public static class ClipboardService
{
    private const uint CF_DIB = 8;
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int DibHeaderSize = 40;
    private const int OpenTimeoutMs = 3000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    // Chrome, Discord, Telegram and image editors prefer PNG over CF_DIB.
    private static readonly uint CF_PNG = RegisterClipboardFormatW("PNG");

    public static Task SetTextAsync(string text) => Task.Run(() => SetText(text));

    public static void SetText(string text)
    {
        var data = new byte[(text.Length + 1) * 2];
        Encoding.Unicode.GetBytes(text, 0, text.Length, data, 0);
        Set((CF_UNICODETEXT, data));
    }

    public static void SetImage(ScreenshotRenderer.RenderedImage image)
    {
        var dib = BuildDib(image);
        if (CF_PNG == 0)
        {
            Set((CF_DIB, dib));
            return;
        }
        var png = ScreenshotRenderer.Encode(image.Bgra, image.Width, image.Height, ScreenshotRenderer.OutputFormat.Png, 100);
        Set((CF_DIB, dib), (CF_PNG, png));
    }

    // Bottom-up 32bpp BI_RGB: the one DIB layout every reader handles.
    internal static byte[] BuildDib(ScreenshotRenderer.RenderedImage image)
    {
        int stride = image.Width * 4;
        var dib = new byte[DibHeaderSize + stride * image.Height];
        var header = dib.AsSpan(0, DibHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[0..], DibHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], image.Height);
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], stride * image.Height);
        for (int y = 0; y < image.Height; y++)
            Buffer.BlockCopy(image.Bgra, y * stride, dib, DibHeaderSize + (image.Height - 1 - y) * stride, stride);
        return dib;
    }

    private static void Set(params (uint Format, byte[] Data)[] items)
    {
        // EmptyClipboard with a NULL owner makes SetClipboardData fail, so own it with a throwaway window.
        IntPtr owner = CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            Open(owner);
            try
            {
                if (!EmptyClipboard()) throw new Win32Exception();
                foreach (var (format, data) in items) Put(format, data);
            }
            finally { CloseClipboard(); }
        }
        finally
        {
            if (owner != IntPtr.Zero) DestroyWindow(owner);
        }
    }

    // Clipboard managers, RDP and slow listeners hold the clipboard open for a while under load.
    private static void Open(IntPtr owner)
    {
        long deadline = Environment.TickCount64 + OpenTimeoutMs;
        while (!OpenClipboard(owner))
        {
            int err = Marshal.GetLastWin32Error();
            if (Environment.TickCount64 > deadline)
                throw new Win32Exception(err, $"Clipboard stayed busy for {OpenTimeoutMs} ms (held by {DescribeHolder()}).");
            Thread.Sleep(30);
        }
    }

    private static string DescribeHolder()
    {
        try
        {
            IntPtr hwnd = GetOpenClipboardWindow();
            if (hwnd == IntPtr.Zero) return "unknown";
            GetWindowThreadProcessId(hwnd, out uint pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch { return "unknown"; }
    }

    private static void Put(uint format, byte[] data)
    {
        IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (h == IntPtr.Zero) throw new OutOfMemoryException($"GlobalAlloc({data.Length}) failed for clipboard data.");
        try
        {
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) throw new Win32Exception();
            try { Marshal.Copy(data, 0, p, data.Length); }
            finally { GlobalUnlock(h); }
            if (SetClipboardData(format, h) == IntPtr.Zero) throw new Win32Exception();
            h = IntPtr.Zero; // the clipboard owns it now
        }
        finally
        {
            if (h != IntPtr.Zero) GlobalFree(h);
        }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string lpszFormat);
    [DllImport("user32.dll")] private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);
}
