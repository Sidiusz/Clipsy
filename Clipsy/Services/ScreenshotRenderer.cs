using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Clipsy.Drawing;
using Microsoft.Graphics.Canvas;
using SkiaSharp;
using Rect = Windows.Foundation.Rect;

namespace Clipsy.Services;

/// <summary>Rasterizes a selection (cropped frozen pixels + burned-in drawings) into an encoded buffer.
/// Drawings go through the same Win2D code as the overlay, so the file matches what was on screen.</summary>
public static class ScreenshotRenderer
{
    public enum OutputFormat { Png, Jpeg, Webp }

    public readonly record struct PixelRect(int X, int Y, int Width, int Height);

    /// <summary>Opaque BGRA pixels, top-down rows.</summary>
    public readonly record struct RenderedImage(byte[] Bgra, int Width, int Height);

    public static OutputFormat ParseFormat(string s)
    {
        return s?.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => OutputFormat.Jpeg,
            "webp" => OutputFormat.Webp,
            _ => OutputFormat.Png,
        };
    }

    public static string ExtensionFor(OutputFormat fmt) => fmt switch
    {
        OutputFormat.Jpeg => ".jpg",
        OutputFormat.Webp => ".webp",
        _ => ".png",
    };

    // WebP stores each side in 14 bits.
    private const int WebpMaxSide = 16383;
    // Burn tiles stay well under every GPU's texture limit and a few hundred MB of VRAM.
    private const int MaxBurnTile = 8192;

    /// <summary>The format <see cref="Encode"/> actually writes: WebP can't hold a side over
    /// 16383 px, so such images become PNG. Callers name the file after this.</summary>
    public static OutputFormat EffectiveFormat(OutputFormat requested, int width, int height)
        => requested == OutputFormat.Webp && (width > WebpMaxSide || height > WebpMaxSide)
            ? OutputFormat.Png
            : requested;

    /// <summary>Same rounding as the overlay's selection hole, clamped to the frame.</summary>
    public static PixelRect ToPixelRect(Rect selectionDip, double dpiScale, int frameWidth, int frameHeight)
    {
        int left = Math.Clamp((int)Math.Round(selectionDip.X * dpiScale), 0, frameWidth);
        int top = Math.Clamp((int)Math.Round(selectionDip.Y * dpiScale), 0, frameHeight);
        int right = Math.Clamp((int)Math.Round((selectionDip.X + selectionDip.Width) * dpiScale), 0, frameWidth);
        int bottom = Math.Clamp((int)Math.Round((selectionDip.Y + selectionDip.Height) * dpiScale), 0, frameHeight);
        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <param name="selectionDip">Selection rect in overlay-window DIPs.</param>
    /// <param name="dpiScale">Scale factor that converts DIPs to source-bitmap pixels.</param>
    public static byte[] RenderPng(
        ScreenFreezeService.FrozenFrame frame,
        Rect selectionDip,
        IReadOnlyList<DrawElement> elements,
        double dpiScale)
        => RenderEncoded(frame, selectionDip, elements, dpiScale, OutputFormat.Png);

    public static byte[] RenderEncoded(
        ScreenFreezeService.FrozenFrame frame,
        Rect selectionDip,
        IReadOnlyList<DrawElement> elements,
        double dpiScale,
        OutputFormat format,
        int quality = 90)
    {
        var image = Render(frame, selectionDip, elements, dpiScale);
        return Encode(image.Bgra, image.Width, image.Height, format, quality);
    }

    public static RenderedImage Render(
        ScreenFreezeService.FrozenFrame frame,
        Rect selectionDip,
        IReadOnlyList<DrawElement> elements,
        double dpiScale)
    {
        var rect = ToPixelRect(selectionDip, dpiScale, frame.PixelWidth, frame.PixelHeight);
        if (rect.Width == 0 || rect.Height == 0)
            throw new InvalidOperationException("Selection is outside the captured frame.");
        var pixels = Crop(frame, rect);
        if (elements.Count > 0)
            pixels = BurnDrawings(pixels, rect, elements, dpiScale);
        return new RenderedImage(pixels, rect.Width, rect.Height);
    }

    public static byte[] Crop(ScreenFreezeService.FrozenFrame frame, PixelRect rect)
    {
        int rowBytes = rect.Width * 4;
        var output = GC.AllocateUninitializedArray<byte>(rowBytes * rect.Height);
        int srcStride = frame.PixelWidth * 4;
        for (int y = 0; y < rect.Height; y++)
        {
            Buffer.BlockCopy(frame.PixelBytes, (rect.Y + y) * srcStride + rect.X * 4,
                output, y * rowBytes, rowBytes);
        }
        return output;
    }

    private static byte[] BurnDrawings(byte[] pixels, PixelRect rect, IReadOnlyList<DrawElement> elements, double scale)
    {
        try { return BurnDrawings(CanvasDevice.GetSharedDevice(), pixels, rect, elements, scale); }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // GPU removed/reset: WARP renders the same output on the CPU.
            Diagnostics.Log("Screenshot render: GPU device failed, using software renderer", ex);
            using var device = new CanvasDevice(forceSoftwareRenderer: true);
            return BurnDrawings(device, pixels, rect, elements, scale);
        }
    }

    private static byte[] BurnDrawings(CanvasDevice device, byte[] pixels, PixelRect rect,
        IReadOnlyList<DrawElement> elements, double scale)
    {
        int tile = Math.Min(device.MaximumBitmapSizeInPixels, MaxBurnTile);
        if (rect.Width <= tile && rect.Height <= tile)
            return BurnTile(device, pixels, rect, elements, scale);

        // Larger than one GPU texture (big multi-monitor frames): burn tile by tile and stitch.
        // Into a new buffer: the software retry after a mid-loop GPU failure needs the clean input.
        int stride = rect.Width * 4;
        var output = new byte[pixels.Length];
        for (int ty = 0; ty < rect.Height; ty += tile)
        for (int tx = 0; tx < rect.Width; tx += tile)
        {
            var part = new PixelRect(rect.X + tx, rect.Y + ty,
                Math.Min(tile, rect.Width - tx), Math.Min(tile, rect.Height - ty));
            int partStride = part.Width * 4;
            var partPixels = new byte[partStride * part.Height];
            for (int y = 0; y < part.Height; y++)
                Buffer.BlockCopy(pixels, (ty + y) * stride + tx * 4, partPixels, y * partStride, partStride);
            var burned = BurnTile(device, partPixels, part, elements, scale);
            for (int y = 0; y < part.Height; y++)
                Buffer.BlockCopy(burned, y * partStride, output, (ty + y) * stride + tx * 4, partStride);
        }
        return output;
    }

    // `rect` is where these pixels sit in the frame; elements are mapped from overlay DIPs onto it.
    private static byte[] BurnTile(CanvasDevice device, byte[] pixels, PixelRect rect,
        IReadOnlyList<DrawElement> elements, double scale)
    {
        using var background = CanvasBitmap.CreateFromBytes(device, pixels, rect.Width, rect.Height,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, 96f, CanvasAlphaMode.Premultiplied);
        using var target = new CanvasRenderTarget(device, rect.Width, rect.Height, 96f);
        using (var ds = target.CreateDrawingSession())
        {
            ds.DrawImage(background);
            // Elements live in overlay DIPs; map the crop origin to 0,0 and DIPs to pixels.
            ds.Transform = Matrix3x2.CreateTranslation((float)(-rect.X / scale), (float)(-rect.Y / scale))
                         * Matrix3x2.CreateScale((float)scale);
            foreach (var el in elements)
            {
                try { DrawingController.Render(ds, el); }
                catch (Exception ex) { Diagnostics.Log("Screenshot render: element skipped", ex); }
            }
        }
        return target.GetPixelBytes();
    }

    /// <summary>Encodes as <see cref="EffectiveFormat"/> of <paramref name="format"/>.</summary>
    public static byte[] Encode(byte[] bgra, int width, int height, OutputFormat format, int quality)
    {
        var encoded = EncodeSegment(bgra, width, height, format, quality);
        return encoded.Count == encoded.Array!.Length ? encoded.Array : encoded.ToArray();
    }

    /// <summary>Like <see cref="Encode"/>, but hands out the encoder's own buffer instead of
    /// copying it to an exact-size array (multi-monitor PNGs run to tens of MB).</summary>
    public static ArraySegment<byte> EncodeSegment(byte[] bgra, int width, int height, OutputFormat format, int quality)
    {
        format = EffectiveFormat(format, width, height);
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            IntPtr scan0 = handle.AddrOfPinnedObject();
            if (format == OutputFormat.Webp)
            {
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                using var pixmap = new SKPixmap(info, scan0, width * 4);
                using var data = pixmap.Encode(SKEncodedImageFormat.Webp, Math.Clamp(quality, 1, 100))
                    ?? throw new InvalidOperationException("Failed to encode WebP screenshot.");
                return data.ToArray();
            }

            // 32bppRgb ignores alpha: PNG is written as RGB and JPEG gets no tinted alpha.
            using var bmp = new Bitmap(width, height, width * 4, PixelFormat.Format32bppRgb, scan0);
            // A quarter byte per pixel fits most screenshot PNGs without a large up-front allocation.
            using var ms = new MemoryStream(format == OutputFormat.Png ? (int)Math.Min((long)width * height / 4, Array.MaxLength) : 0);
            if (format == OutputFormat.Jpeg && GetEncoder(ImageFormat.Jpeg) is { } jpeg)
            {
                using var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Clamp((long)quality, 1L, 100L));
                bmp.Save(ms, jpeg, ep);
            }
            else
            {
                bmp.Save(ms, format == OutputFormat.Jpeg ? ImageFormat.Jpeg : ImageFormat.Png);
            }
            return new ArraySegment<byte>(ms.GetBuffer(), 0, (int)ms.Length);
        }
        finally { handle.Free(); }
    }

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        foreach (var c in ImageCodecInfo.GetImageEncoders())
        {
            if (c.FormatID == format.Guid) return c;
        }
        return null;
    }
}
