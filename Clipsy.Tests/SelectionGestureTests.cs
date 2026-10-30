using Clipsy.Services;
using Windows.Foundation;
using Xunit;

namespace Clipsy.Tests;

public class SelectionGestureTests
{
    [Theory]
    [InlineData(100, 100, false)]
    [InlineData(103, 97, false)]
    [InlineData(104, 100, true)]
    [InlineData(100, 96, true)]
    public void Pointer_jitter_stays_a_click(double x, double y, bool expected)
        => Assert.Equal(expected, SelectionGesture.IsDrag(new Point(100, 100), new Point(x, y)));

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(300, 0, false)]
    [InlineData(0, 300, false)]
    [InlineData(300, 3, false)]
    [InlineData(4, 4, true)]
    public void Empty_or_thin_regions_are_not_selections(double w, double h, bool expected)
        => Assert.Equal(expected, SelectionGesture.IsUsable(new Rect(10, 10, w, h)));
}
