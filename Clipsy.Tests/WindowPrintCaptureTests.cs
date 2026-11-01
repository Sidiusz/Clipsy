using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

public class WindowPrintCaptureTests
{
    [Fact]
    public void Black_pixels_count_as_blank_whatever_their_alpha()
    {
        Assert.True(WindowPrintCapture.IsBlank(new byte[] { 0, 0, 0, 255, 0, 0, 0, 0 }));
        Assert.False(WindowPrintCapture.IsBlank(new byte[] { 0, 0, 0, 255, 0, 1, 0, 255 }));
    }

    [Fact]
    public void Captured_window_matches_its_visible_bounds()
    {
        foreach (var target in WindowTargets.VisibleInZOrder())
        {
            if (WindowPrintCapture.Capture(target.Hwnd, target.Bounds) is not { } image) continue;
            Assert.Equal(target.Bounds.Width, image.Width);
            Assert.Equal(target.Bounds.Height, image.Height);
            return;
        }
    }
}
