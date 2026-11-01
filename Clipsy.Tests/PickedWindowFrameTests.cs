using System;
using System.Drawing;
using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

public class PickedWindowFrameTests
{
    private static ScreenFreezeService.FrozenFrame Frame(int w, int h, int originX = 0) => new()
    {
        PixelBytes = new byte[w * h * 4],
        PixelWidth = w,
        PixelHeight = h,
        VirtualBounds = new Rectangle(originX, 0, w, h),
        Monitors = Array.Empty<ScreenFreezeService.MonitorInfo>(),
    };

    private static ScreenshotRenderer.RenderedImage Image(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            px[i] = (byte)x; px[i + 1] = (byte)y; px[i + 2] = 0xEE; px[i + 3] = 0xFF;
        }
        return new ScreenshotRenderer.RenderedImage(px, w, h);
    }

    private static (byte X, byte Y, byte Mark) At(ScreenFreezeService.FrozenFrame f, int x, int y)
    {
        int i = (y * f.PixelWidth + x) * 4;
        return (f.PixelBytes[i], f.PixelBytes[i + 1], f.PixelBytes[i + 2]);
    }

    [Fact]
    public void Window_pixels_replace_the_covered_area_only()
    {
        var frame = Frame(20, 20);
        var result = ScreenFreezeService.WithImageAt(frame, Image(5, 4), new Rectangle(3, 2, 5, 4));

        Assert.Equal((0, 0, 0xEE), At(result, 3, 2));
        Assert.Equal((4, 3, 0xEE), At(result, 7, 5));
        Assert.Equal((0, 0, 0), At(result, 8, 5));
        Assert.Equal((0, 0, 0), At(result, 2, 2));
        Assert.All(frame.PixelBytes, b => Assert.Equal(0, b)); // the original stays untouched
    }

    [Fact]
    public void Larger_capture_is_centered_and_offscreen_parts_are_clipped()
    {
        // Maximized window: 2 px of invisible border on each side; it also hangs off the left edge.
        var frame = Frame(10, 10, originX: 100);
        var result = ScreenFreezeService.WithImageAt(frame, Image(8, 8), new Rectangle(98, 0, 4, 4));

        Assert.Equal((4, 2, 0xEE), At(result, 0, 0));
        Assert.Equal((5, 5, 0xEE), At(result, 1, 3));
        Assert.Equal((0, 0, 0), At(result, 2, 0));
    }
}
