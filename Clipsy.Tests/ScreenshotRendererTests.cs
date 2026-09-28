using System.Drawing;
using System.IO;
using Clipsy.Drawing;
using Clipsy.Services;
using Xunit;
using Rect = Windows.Foundation.Rect;

namespace Clipsy.Tests;

public class ScreenshotRendererTests
{
    // Opaque grey frame with a unique blue value per pixel column so crops are verifiable.
    private static ScreenFreezeService.FrozenFrame Frame(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            px[i] = (byte)x; px[i + 1] = (byte)y; px[i + 2] = 0x40; px[i + 3] = 0xFF;
        }
        return new ScreenFreezeService.FrozenFrame
        {
            PixelBytes = px, PixelWidth = w, PixelHeight = h,
            VirtualBounds = new Rectangle(0, 0, w, h),
            Monitors = Array.Empty<ScreenFreezeService.MonitorInfo>(),
        };
    }

    [Fact]
    public void PixelRectMatchesOverlayRounding()
    {
        var r = ScreenshotRenderer.ToPixelRect(new Rect(10.3, 10.3, 100, 100), 1.5, 1000, 1000);
        Assert.Equal(new ScreenshotRenderer.PixelRect(15, 15, 150, 150), r);
    }

    [Fact]
    public void PixelRectIsClampedToFrame()
    {
        var r = ScreenshotRenderer.ToPixelRect(new Rect(-20, 90, 50, 50), 1.0, 100, 100);
        Assert.Equal(new ScreenshotRenderer.PixelRect(0, 90, 30, 10), r);
    }

    [Fact]
    public void CropCopiesExpectedPixels()
    {
        var f = Frame(10, 10);
        var crop = ScreenshotRenderer.Crop(f, new ScreenshotRenderer.PixelRect(3, 4, 2, 2));
        Assert.Equal(16, crop.Length);
        Assert.Equal(3, crop[0]);      // x of first pixel
        Assert.Equal(4, crop[1]);      // y of first pixel
        Assert.Equal(4, crop[4]);      // next column
        Assert.Equal(5, crop[8 + 1]);  // second row
    }

    [Fact]
    public void PngRoundTripsWithoutDrawings()
    {
        var f = Frame(64, 32);
        var png = ScreenshotRenderer.RenderPng(f, new Rect(8, 4, 16, 8), Array.Empty<DrawElement>(), 1.0);
        using var bmp = new Bitmap(new MemoryStream(png));
        Assert.Equal(16, bmp.Width);
        Assert.Equal(8, bmp.Height);
        var c = bmp.GetPixel(0, 0);
        Assert.Equal(8, c.B);
        Assert.Equal(4, c.G);
    }

    [Fact]
    public void DrawingsAreOffsetByCropOrigin()
    {
        var f = Frame(200, 200);
        // Filled-looking thick rectangle outline at DIP (60,60); selection starts at DIP 50 with scale 2.
        var rect = new RectangleElement { Bounds = new Rect(60, 60, 10, 10), Thickness = 4, Color = Microsoft.UI.Colors.Red };
        var png = ScreenshotRenderer.RenderPng(f, new Rect(50, 50, 40, 40), new DrawElement[] { rect }, 2.0);
        using var bmp = new Bitmap(new MemoryStream(png));
        Assert.Equal(80, bmp.Width);
        // Left edge of the rect is at (60-50)*2 = 20px in the output.
        var onEdge = bmp.GetPixel(20, 30);
        Assert.True(onEdge.R > 200 && onEdge.G < 60, $"expected red at the rect edge, got {onEdge}");
        var outside = bmp.GetPixel(5, 5);
        Assert.Equal(0x40, outside.R);
    }
}
