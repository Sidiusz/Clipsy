using System.Diagnostics;
using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

public class Win32ResizeOverlayTests
{
    // Off-screen, so nothing shows. A drag sends one of these per mouse move on the UI thread.
    [Fact]
    public void Resizing_a_large_region_fits_inside_a_frame()
    {
        var overlay = new Win32ResizeOverlay();
        Assert.True(overlay.Create(-30000, -30000, 2560, 1440));
        try
        {
            var sw = Stopwatch.StartNew();
            const int moves = 200;
            for (int i = 0; i < moves; i++)
                overlay.MoveTo(-30000, -30000, 2560 - i % 40, 1440 - i % 30);
            Assert.True(sw.ElapsedMilliseconds < moves * 16, $"{sw.ElapsedMilliseconds / (double)moves:F1} ms per move");
        }
        finally { overlay.Destroy(); }
    }
}
