using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace Clipsy.Services;

/// <summary>Win32 clipboard writes with every format rendered up front: safe on any thread, and apps
/// reading the clipboard never call back into Clipsy (the OLE path blocked the UI thread under load).</summary>
public static class ClipboardService
{
    private const uint CF_DIB = 8;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const int DropFilesHeaderSize = 20;
    private const int DROPEFFECT_COPY = 1;
    private const uint GMEM_MOVEABLE = 0x0002;
    private const int DibHeaderSize = 40;
    private const int OpenTimeoutMs = 3000;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    // Chrome, Discord, Telegram and image editors prefer PNG over CF_DIB.
    private static readonly uint CF_PNG = RegisterClipboardFormatW("PNG");
    // Without it Explorer may treat a pasted file as a move.
    private static readonly uint CF_PREFERRED_DROPEFFECT = RegisterClipboardFormatW("Preferred DropEffect");
    // Above this a PNG copy costs seconds of encoding and hundreds of MB; CF_DIB alone still pastes.
    private const long MaxPngPixels = 50_000_000;

    // Fills the locked clipboard memory in place, so large images aren't staged in a second array.
    private delegate void Fill(Span<byte> dest);
    private readonly record struct ClipItem(uint Format, int Length, Fill Write);

    private static ClipItem FromBytes(uint format, ArraySegment<byte> data)
        => new(format, data.Count, dest => data.AsSpan().CopyTo(dest));

    public static Task SetTextAsync(string text) => Task.Run(() => SetText(text));

    public static void SetText(string text)
    {
        var data = new byte[(text.Length + 1) * 2];
        Encoding.Unicode.GetBytes(text, 0, text.Length, data, 0);
        Set(FromBytes(CF_UNICODETEXT, data));
    }

    public static void SetImage(ScreenshotRenderer.RenderedImage image) => Set(ImageItems(image, null).ToArray());

    /// <summary>A saved screenshot as both pixels and the file, so it pastes into chats and into Explorer.</summary>
    public static void SetImageFile(string path)
    {
        var image = DecodeFile(path);
        byte[]? png = IsPng(path) ? File.ReadAllBytes(path) : null;
        var items = ImageItems(image, png);
        items.AddRange(FileItems(path));
        Set(items.ToArray());
    }

    private static List<ClipItem> ImageItems(ScreenshotRenderer.RenderedImage image, byte[]? encodedPng)
    {
        var items = new List<ClipItem> { new(CF_DIB, DibLength(image), dest => WriteDib(image, dest)) };
        if (CF_PNG == 0) return items;
        if (encodedPng != null) items.Add(FromBytes(CF_PNG, encodedPng));
        else if ((long)image.Width * image.Height <= MaxPngPixels)
            items.Add(FromBytes(CF_PNG, ScreenshotRenderer.EncodeSegment(image.Bgra, image.Width, image.Height, ScreenshotRenderer.OutputFormat.Png, 100)));
        return items;
    }

    private static bool IsPng(string path) => Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase);

    private static ScreenshotRenderer.RenderedImage DecodeFile(string path)
    {
        using var decoded = SKBitmap.Decode(path) ?? throw new InvalidDataException($"Could not decode {path}.");
        var info = new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bgra = new SKBitmap(info);
        if (!decoded.CopyTo(bgra, SKColorType.Bgra8888)) throw new InvalidDataException($"Could not convert {path}.");
        var pixels = bgra.Bytes;
        ScreenFreezeService.ForceOpaque(pixels);
        return new ScreenshotRenderer.RenderedImage(pixels, info.Width, info.Height);
    }

    internal static byte[] BuildDropFiles(string path)
    {
        var full = Path.GetFullPath(path);
        var data = new byte[DropFilesHeaderSize + (full.Length + 2) * 2];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), DropFilesHeaderSize); // pFiles
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 1);                   // fWide
        Encoding.Unicode.GetBytes(full, 0, full.Length, data, DropFilesHeaderSize);     // then two NULs
        return data;
    }

    private static IEnumerable<ClipItem> FileItems(string path)
    {
        yield return FromBytes(CF_HDROP, BuildDropFiles(path));
        if (CF_PREFERRED_DROPEFFECT != 0)
            yield return FromBytes(CF_PREFERRED_DROPEFFECT, BitConverter.GetBytes(DROPEFFECT_COPY));
    }

    private static int DibLength(ScreenshotRenderer.RenderedImage image)
        => checked(DibHeaderSize + image.Width * 4 * image.Height);

    internal static byte[] BuildDib(ScreenshotRenderer.RenderedImage image)
    {
        var dib = new byte[DibLength(image)];
        WriteDib(image, dib);
        return dib;
    }

    // Bottom-up 32bpp BI_RGB: the one DIB layout every reader handles.
    private static void WriteDib(ScreenshotRenderer.RenderedImage image, Span<byte> dib)
    {
        int stride = image.Width * 4;
        var header = dib[..DibHeaderSize];
        header.Clear(); // locked clipboard memory isn't zeroed
        BinaryPrimitives.WriteInt32LittleEndian(header[0..], DibHeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], image.Height);
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], stride * image.Height);
        for (int y = 0; y < image.Height; y++)
            image.Bgra.AsSpan(y * stride, stride).CopyTo(dib.Slice(DibHeaderSize + (image.Height - 1 - y) * stride, stride));
    }

    private static void Set(params ClipItem[] items)
    {
        // EmptyClipboard with a NULL owner makes SetClipboardData fail, so own it with a throwaway window.
        IntPtr owner = CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            Open(owner);
            try
            {
                if (!EmptyClipboard()) throw new Win32Exception();
                foreach (var item in items) Put(item);
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

    private static unsafe void Put(ClipItem item)
    {
        IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)item.Length);
        if (h == IntPtr.Zero) throw new OutOfMemoryException($"GlobalAlloc({item.Length}) failed for clipboard data.");
        try
        {
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero) throw new Win32Exception();
            try { item.Write(new Span<byte>((void*)p, item.Length)); }
            finally { GlobalUnlock(h); }
            if (SetClipboardData(item.Format, h) == IntPtr.Zero) throw new Win32Exception();
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
