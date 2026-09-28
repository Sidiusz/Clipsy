using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

/// <summary>Conversions of a finished recording. The ffmpeg cases use the ffmpeg.exe the app
/// downloaded (%LOCALAPPDATA%\Clipsy\ffmpeg) and are skipped without it.</summary>
public sealed class VideoPipelineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clipsy-video-" + Guid.NewGuid().ToString("N"));

    public VideoPipelineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // H.264 + AAC MP4 like ScreenRecorderLib writes, with the index at the front (faststart).
    private string MakeClip(double seconds = 2)
    {
        var path = Path.Combine(_dir, "clip.mp4");
        var psi = new ProcessStartInfo(FFmpegService.Instance.ExePath) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-f", "lavfi", "-i", $"testsrc=size=320x180:rate=30:duration={seconds}",
                     "-f", "lavfi", "-i", $"sine=frequency=440:duration={seconds}",
                     "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart", "-y", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
        return path;
    }

    [Theory]
    [InlineData("VP9")]
    [InlineData("AV1")]
    public async Task TranscodesToModernCodecs(string codec)
    {
        if (!FFmpegService.Instance.IsAvailable) return;
        var output = Path.Combine(_dir, codec + ".mkv");
        Assert.True(await FFmpegService.Instance.TranscodeAsync(MakeClip(), output, codec, 2));
        Assert.True(new FileInfo(output).Length > 1000);
    }

    [Fact]
    public async Task ConvertsToGifAndRemuxes()
    {
        if (!FFmpegService.Instance.IsAvailable) return;
        var clip = MakeClip();
        var gif = Path.Combine(_dir, "out.gif");
        Assert.True(await FFmpegService.Instance.ConvertToGifAsync(clip, gif));
        using (var img = Image.FromFile(gif))
            Assert.True(img.GetFrameCount(FrameDimension.Time) > 10);

        var avi = Path.Combine(_dir, "out.avi");
        Assert.True(await FFmpegService.Instance.RemuxAsync(clip, avi));
        Assert.True(new FileInfo(avi).Length > 1000);
    }

    [Fact]
    public async Task NativeEncoderWritesValidAnimatedGif()
    {
        if (!FFmpegService.Instance.IsAvailable) return; // needs a sample clip
        var gif = Path.Combine(_dir, "native.gif");
        var (ok, truncated) = await NativeGifEncoder.ConvertMp4ToGifAsync(MakeClip(1), gif);
        Assert.True(ok);
        Assert.False(truncated);
        using var img = Image.FromFile(gif);
        Assert.Equal(320, img.Width);
        Assert.InRange(img.GetFrameCount(FrameDimension.Time), 8, 20);
    }

    [Fact]
    public void GifWriterRoundTripsPixels()
    {
        const int w = 40, h = 20;
        var frames = new List<byte[]>();
        for (int f = 0; f < 3; f++)
        {
            var rgb = new byte[w * h * 3];
            for (int i = 0; i < w * h; i++)
            {
                bool isLeft = i % w < w / 2;
                rgb[i * 3] = (byte)(isLeft ? 255 : 0);
                rgb[i * 3 + 1] = (byte)(f * 100);
                rgb[i * 3 + 2] = (byte)(isLeft ? 0 : 255);
            }
            frames.Add(rgb);
        }
        var palette = NativeGifEncoder.BuildPalette(frames, 16);
        var path = Path.Combine(_dir, "writer.gif");
        using (var fs = File.Create(path))
        {
            var gif = new NativeGifEncoder.GifWriter(fs, w, h, palette, dither: false);
            foreach (var f in frames) gif.AddFrame(f, 8);
            gif.Finish();
        }
        using var bmp = new Bitmap(path);
        Assert.Equal(3, bmp.GetFrameCount(FrameDimension.Time));
        bmp.SelectActiveFrame(FrameDimension.Time, 2);
        var left = bmp.GetPixel(2, 2);
        var right = bmp.GetPixel(w - 3, h - 3);
        Assert.True(left.R > 200 && left.B < 50, left.ToString());
        Assert.True(right.B > 200 && right.R < 50, right.ToString());
    }

    [Fact]
    public void FrameDelaysDoNotDrift()
    {
        int total = 0;
        for (int i = 0; i < 12; i++) total += NativeGifEncoder.FrameDelayCs(i, 12);
        Assert.Equal(100, total);
    }

    [Fact]
    public void Mp4IndexDetection()
    {
        if (!FFmpegService.Instance.IsAvailable) return;
        var clip = MakeClip(1);
        Assert.True(RecordingController.Mp4HasIndex(clip));

        var truncated = Path.Combine(_dir, "broken.mp4");
        var bytes = File.ReadAllBytes(clip);
        // Drop the front 'moov' by keeping only 'ftyp' and a raw tail, like a killed recording.
        File.WriteAllBytes(truncated, bytes.Take(32).Concat(new byte[] { 0, 0, 0, 0, (byte)'m', (byte)'d', (byte)'a', (byte)'t' }).ToArray());
        Assert.False(RecordingController.Mp4HasIndex(truncated));
    }
}
