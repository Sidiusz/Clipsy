using System.Drawing;
using Clipsy.Services;
using Xunit;
using Drag = Clipsy.Services.Win32ResizeOverlay.DragMode;

namespace Clipsy.Tests;

public class RecordingRegionTests
{
    [Theory]
    [InlineData("720p", 2560, 1440, 1280, 720)]
    [InlineData("1080p", 2560, 1440, 1920, 1080)]
    [InlineData("480p", 2560, 1440, 852, 480)]
    [InlineData("720p", 1300, 900, 1040, 720)]
    [InlineData("720p", 640, 480, 640, 480)]   // never upscaled
    [InlineData("native", 1301, 901, 1301, 901)]
    public void Output_size_scales_down_to_the_chosen_height(string res, int w, int h, int ow, int oh)
        => Assert.Equal((ow, oh), RecordingService.ResolveOutputSize(res, w, h));

    [Fact]
    public void Crop_with_the_frames_shape_is_untouched()
        => Assert.Equal(new Rectangle(10, 20, 1280, 720), RecordingService.FitAspect(new Rectangle(10, 20, 1280, 720), 16.0 / 9));

    [Fact]
    public void Wider_crop_is_trimmed_at_both_sides()
        => Assert.Equal(new Rectangle(140, 0, 1600, 900), RecordingService.FitAspect(new Rectangle(0, 0, 1880, 900), 16.0 / 9));

    [Fact]
    public void Taller_crop_is_trimmed_at_top_and_bottom()
        => Assert.Equal(new Rectangle(0, 50, 1600, 900), RecordingService.FitAspect(new Rectangle(0, 0, 1600, 1000), 16.0 / 9));

    [Fact]
    public void Corner_resize_keeps_the_shape_and_the_opposite_corner()
    {
        var (x, y, w, h) = Win32ResizeOverlay.KeepAspect(Drag.ResizeTL, 1000, 500, 100, 100, 800, 450, 16.0 / 9);
        Assert.Equal((1000, 562), (w, h));
        Assert.Equal((100 + 800, 100 + 450), (x + w, y + h));
    }

    [Fact]
    public void Edge_resize_keeps_the_shape_centred()
    {
        var (x, y, w, h) = Win32ResizeOverlay.KeepAspect(Drag.ResizeR, 1600, 450, 100, 100, 800, 450, 16.0 / 9);
        Assert.Equal((100, 1600, 900), (x, w, h));
        Assert.Equal(100 + 450 / 2, y + h / 2);
    }

    [Fact]
    public void Resize_never_goes_below_the_minimum()
    {
        var (_, _, w, h) = Win32ResizeOverlay.KeepAspect(Drag.ResizeBR, 10, 10, 0, 0, 800, 450, 16.0 / 9);
        Assert.True(w >= 64 && h >= 64);
    }
}
