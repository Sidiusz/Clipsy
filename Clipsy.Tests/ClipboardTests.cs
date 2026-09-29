using System;
using System.Buffers.Binary;
using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

public class ClipboardTests
{
    [Fact]
    public void Dib_has_standard_header_and_bottom_up_rows()
    {
        const int w = 3, h = 2;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            px[i] = (byte)x; px[i + 1] = (byte)y; px[i + 2] = 0x40; px[i + 3] = 0xFF;
        }

        var dib = ClipboardService.BuildDib(new ScreenshotRenderer.RenderedImage(px, w, h));

        var header = dib.AsSpan(0, 40);
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(header));
        Assert.Equal(w, BinaryPrimitives.ReadInt32LittleEndian(header[4..]));
        Assert.Equal(h, BinaryPrimitives.ReadInt32LittleEndian(header[8..]));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(header[12..]));
        Assert.Equal(32, BinaryPrimitives.ReadInt16LittleEndian(header[14..]));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(header[16..]));
        Assert.Equal(40 + w * h * 4, dib.Length);

        // First stored row is the image's last row.
        Assert.Equal(h - 1, dib[40 + 1]);
        Assert.Equal(0, dib[40 + w * 4 + 1]);
        Assert.Equal(2, dib[40 + 2 * 4]);
    }
}
