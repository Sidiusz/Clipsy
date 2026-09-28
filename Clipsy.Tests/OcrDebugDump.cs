using Clipsy.Services;
using Xunit;
using Xunit.Abstractions;

namespace Clipsy.Tests;

public class OcrDebugDump(ITestOutputHelper output)
{
    [Fact(Skip = "diagnostic: set OCR_SAMPLE and remove Skip")]
    public async Task Dump()
    {
        var sample = OcrQualityTests.Samples().First(s => s.Name == Environment.GetEnvironmentVariable("OCR_SAMPLE"));
        foreach (IOcrEngine engine in new IOcrEngine[] { new WinRtOcrEngine(), new PpOcrV5Engine() })
        {
            output.WriteLine(engine.GetType().Name);
            foreach (var w in await engine.RecognizeAsync(sample.Png))
                output.WriteLine($"  '{w.Text}' {w.BoundsPixels.X:F0},{w.BoundsPixels.Y:F0} {w.BoundsPixels.Width:F0}x{w.BoundsPixels.Height:F0}");
        }
    }
}
