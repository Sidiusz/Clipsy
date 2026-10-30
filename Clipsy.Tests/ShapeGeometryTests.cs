using Clipsy.Drawing;
using Windows.Foundation;
using Xunit;

namespace Clipsy.Tests;

public class ShapeGeometryTests
{
    [Theory]
    [InlineData(80, 30, 100, 100)]
    [InlineData(-80, 30, 70, 100)]
    [InlineData(80, -30, 100, 70)]
    [InlineData(-80, -30, 70, 70)]
    [InlineData(30, 80, 100, 100)]
    [InlineData(-30, -80, 70, 70)]
    public void Shift_creates_equal_sides_in_every_drag_direction(double dx, double dy, double left, double top)
    {
        var start = new Point(100, 100);
        var bounds = ShapeGeometry.Bounds(start, new Point(100 + dx, 100 + dy), true);
        Assert.Equal(30, bounds.Width);
        Assert.Equal(bounds.Width, bounds.Height);
        Assert.Equal(left, bounds.X);
        Assert.Equal(top, bounds.Y);
    }

    [Fact]
    public void Releasing_shift_restores_the_unconstrained_bounds_from_the_raw_pointer()
    {
        var start = new Point(100, 100);
        var end = new Point(130, 180);
        Assert.Equal(new Rect(100, 100, 30, 30), ShapeGeometry.Bounds(start, end, true));
        Assert.Equal(new Rect(100, 100, 30, 80), ShapeGeometry.Bounds(start, end, false));
    }

    [Theory]
    [InlineData(200, 100, 100)]
    [InlineData(180, 80, 100)]
    [InlineData(120, 20, 100)]
    [InlineData(100, 0, 100)]
    [InlineData(80, 20, 80)]
    [InlineData(20, 80, 20)]
    public void Vertical_cursor_motion_shrinks_then_flips_the_shape(double pointerY, double size, double top)
    {
        var result = ShapeGeometry.Bounds(new Point(100, 100), new Point(200, pointerY), true);
        Assert.Equal(new Rect(100, top, size, size), result);
    }

    [Theory]
    [InlineData(200, 100, 100)]
    [InlineData(180, 80, 100)]
    [InlineData(100, 0, 100)]
    [InlineData(80, 20, 80)]
    public void Horizontal_cursor_motion_shrinks_then_flips_the_shape(double pointerX, double size, double left)
    {
        var result = ShapeGeometry.Bounds(new Point(100, 100), new Point(pointerX, 200), true);
        Assert.Equal(new Rect(left, 100, size, size), result);
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(14, 15)]
    [InlineData(29, 30)]
    [InlineData(44, 45)]
    [InlineData(89, 90)]
    [InlineData(119, 120)]
    [InlineData(-17, -15)]
    [InlineData(-91, -90)]
    [InlineData(179, 180)]
    public void Lines_snap_to_fifteen_degree_steps_without_changing_length(double degrees, double snappedDegrees)
    {
        var start = new Point(40, 70);
        var end = new Point(start.X + 100 * Math.Cos(degrees * Math.PI / 180), start.Y + 100 * Math.Sin(degrees * Math.PI / 180));
        var result = ShapeGeometry.LineEnd(start, end, true);
        // Windows.Foundation.Point stores coordinates at float precision.
        Assert.InRange(Math.Abs(start.X + 100 * Math.Cos(snappedDegrees * Math.PI / 180) - result.X), 0, 0.001);
        Assert.InRange(Math.Abs(start.Y + 100 * Math.Sin(snappedDegrees * Math.PI / 180) - result.Y), 0, 0.001);
        double length = Math.Sqrt(Math.Pow(result.X - start.X, 2) + Math.Pow(result.Y - start.Y, 2));
        Assert.InRange(Math.Abs(length - 100), 0, 0.001);
        Assert.Equal(end, ShapeGeometry.LineEnd(start, end, false));
    }

    [Fact]
    public void Click_without_drag_does_not_create_a_shape_or_line()
    {
        var point = new Point(40, 70);
        Assert.Equal(new Rect(40, 70, 0, 0), ShapeGeometry.Bounds(point, point, true));
        Assert.Equal(point, ShapeGeometry.LineEnd(point, point, true));
    }
}
