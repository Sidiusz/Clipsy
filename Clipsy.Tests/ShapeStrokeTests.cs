using Clipsy.Drawing;
using Windows.Foundation;
using Xunit;

namespace Clipsy.Tests;

public class ShapeStrokeTests
{
    [Fact]
    public void Stroke_stays_inside_the_dragged_bounds_like_the_preview()
    {
        var b = DrawingController.StrokeInside(new Rect(10, 20, 100, 50), 8);
        // Centered on this rect, an 8 px stroke spans exactly 10..110 x 20..70.
        Assert.Equal(new Rect(14, 24, 92, 42), b);
    }

    [Fact]
    public void Thicker_than_the_shape_collapses_instead_of_going_negative()
    {
        var b = DrawingController.StrokeInside(new Rect(0, 0, 4, 30), 10);
        Assert.Equal(0, b.Width);
        Assert.Equal(20, b.Height);
    }
}
