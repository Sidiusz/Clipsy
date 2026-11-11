using Clipsy.Services;
using Windows.Foundation;
using Xunit;

namespace Clipsy.Tests;

public class MonitorSelectionBoundsTests
{
    private static readonly Rect Canvas = new(0, 0, 4480, 1440);
    private static readonly Rect[] Monitors = { new(0, 0, 1920, 1080), new(1920, 0, 2560, 1440) };

    [Theory]
    [InlineData(200, 880)]
    [InlineData(2200, 1240)]
    public void Bottom_edge_matches_each_monitor(double x, double expectedY)
    {
        var result = MonitorSelectionBounds.Move(new Rect(x, 400, 300, 200), x, 1400, Canvas, Monitors);
        Assert.Equal(new Rect(x, expectedY, 300, 200), result);
    }

    [Theory]
    [InlineData(2560, 1440, 1.75)]
    [InlineData(1920, 1080, 1.75)]
    [InlineData(2560, 1440, 1.5)]
    [InlineData(3840, 2160, 1.25)]
    public void Region_pushed_into_a_corner_stays_there_at_fractional_scales(int pxW, int pxH, double scale)
    {
        var screen = new Rect(0, 0, pxW / scale, pxH / scale);
        for (int px = 8; px < 700; px++)
        {
            var region = new Rect(100 / scale, 100 / scale, px / scale, px / scale);
            var moved = MonitorSelectionBounds.Move(region, 1e6, 1e6, screen, new[] { screen });
            Assert.True(moved.X > screen.Width / 4 && moved.Y > screen.Height / 4, $"{px}px region jumped to {moved.X};{moved.Y}");
        }
    }

    [Fact]
    public void Region_can_straddle_the_seam_and_move_to_the_other_monitor()
    {
        var region = new Rect(500, 200, 300, 200);
        Assert.Equal(new Rect(1800, 200, 300, 200), MonitorSelectionBounds.Move(region, 1800, 200, Canvas, Monitors));
        Assert.Equal(new Rect(2200, 1200, 300, 200), MonitorSelectionBounds.Move(region, 2200, 1200, Canvas, Monitors));
    }

    [Fact]
    public void Region_cannot_straddle_the_missing_corner_below_the_small_monitor()
    {
        var result = MonitorSelectionBounds.Move(new Rect(1600, 700, 300, 200), 1850, 1000, Canvas, Monitors);
        Assert.Equal(new Rect(1920, 1000, 300, 200), result);
    }

    [Fact]
    public void Offset_and_negative_monitor_coordinates_are_respected()
    {
        Rect[] monitors = { new(-1920, 360, 1920, 1080), new(0, 0, 2560, 1440) };
        var result = MonitorSelectionBounds.Move(new Rect(-1500, 500, 300, 200), -1500, 0,
            new Rect(-1920, 0, 4480, 1440), monitors);
        Assert.Equal(new Rect(-1500, 360, 300, 200), result);
    }

    [Fact]
    public void Overlapping_monitor_rectangles_do_not_hide_uncovered_pixels()
    {
        Rect[] monitors = { new(0, 0, 1920, 1080), new(0, 0, 1920, 1080) };
        var result = MonitorSelectionBounds.Move(new Rect(100, 500, 300, 200), 100, 1050, Canvas, monitors);
        Assert.Equal(new Rect(100, 880, 300, 200), result);
    }

    [Fact]
    public void Scaled_overlay_coordinates_use_the_same_physical_boundary()
    {
        Rect[] monitors = { new(0, 0, 960, 540), new(960, 0, 1280, 720) };
        var result = MonitorSelectionBounds.Move(new Rect(100, 200, 150, 100), 100, 650,
            new Rect(0, 0, 2240, 720), monitors);
        Assert.Equal(new Rect(100, 440, 150, 100), result);
    }

    [Fact]
    public void Fractional_coordinate_rounding_does_not_create_a_gap_at_the_seam()
    {
        Rect[] monitors = { new(0, 0, 0.3, 1), new(0.1 + 0.2, 0, 1, 1) };
        var region = new Rect(0.2, 0.2, 0.2, 0.2);
        Assert.Equal(region, MonitorSelectionBounds.Move(region, region.X, region.Y, new Rect(0, 0, 1.3, 1), monitors));
    }

    [Fact]
    public void Oversized_region_keeps_its_size_instead_of_being_cropped()
    {
        Assert.Equal(Canvas, MonitorSelectionBounds.Move(Canvas, 100, 200, Canvas, Monitors));
    }
}
