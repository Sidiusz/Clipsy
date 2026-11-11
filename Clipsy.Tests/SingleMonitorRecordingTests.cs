using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Clipsy.Services;
using Xunit;
using Xunit.Abstractions;

namespace Clipsy.Tests;

// Records with only the primary display registered, the way a single-monitor PC does, and
// compares a video frame with the screen. No UI, no sound, but GPU encoding: opt-in.
[Collection("Recording")]
public class SingleMonitorRecordingTests(ITestOutputHelper output)
{
    [Fact]
    public void Region_on_a_single_monitor_records_the_region_not_the_screen_corner()
    {
        if (Environment.GetEnvironmentVariable("CLIPSY_RECORD_TEST") != "1" || !FFmpegService.Instance.IsAvailable) return;
        var settings = SettingsService.Instance.Settings;
        var saved = settings.Clone();
        var primary = ScreenFreezeService.EnumerateMonitors().First(m => m.IsPrimary).Bounds;
        var failures = new List<string>();
        RecordingService.MonitorFilter = r => r == primary;
        try
        {
            settings.MicrophoneEnabled = false;
            settings.CaptureVideoCursor = false;
            settings.VideoCodec = "H.264";
            settings.VideoFramerate = 30;
            foreach (var dda in new[] { false, true })
            foreach (var res in new[] { "Original", "480p" })
            foreach (var size in new[] { new Size(801, 601), new Size(primary.Width, primary.Height), new Size(1280, 720) })
            {
                settings.VideoResolution = res;
                // The screen is live: a case fails only when it fails three times in a row.
                var failed = new List<string>();
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    failed = Record(primary, size, res, dda);
                    if (failed.Count == 0) break;
                }
                failures.AddRange(failed);
            }
        }
        finally
        {
            RecordingService.MonitorFilter = null;
            settings.MicrophoneEnabled = saved.MicrophoneEnabled;
            settings.CaptureVideoCursor = saved.CaptureVideoCursor;
            settings.VideoCodec = saved.VideoCodec;
            settings.VideoFramerate = saved.VideoFramerate;
            settings.VideoResolution = saved.VideoResolution;
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private List<string> Record(Rectangle primary, Size size, string res, bool dda)
    {
        var failures = new List<string>();
        bool full = size.Width == primary.Width;
        var region = new Rectangle(primary.X + (full ? 0 : primary.Width / 3), primary.Y + (full ? 0 : primary.Height / 4), size.Width, size.Height);
        var moved = full ? region : new Rectangle(primary.X + 40, primary.Bottom - size.Height - 40, size.Width, size.Height);
        var shown = new Size(size.Width & ~1, size.Height & ~1); // the recorded part of an odd-sized region
        var (ow, oh) = RecordingService.ResolveOutputSize(res, size.Width, size.Height);
        string name = $"{(dda ? "DDA" : "WGC")} {res} {size.Width}x{size.Height}";
        using var service = new RecordingService();
        string? done = null, error = null;
        var finished = new ManualResetEventSlim();
        service.RecordingComplete += p => { done = p; finished.Set(); };
        service.RecordingFailed += e => { error = e; finished.Set(); };
        try
        {
            service.Start(region.X, region.Y, region.Width, region.Height, desktopDuplication: dda);
            Thread.Sleep(1500);
            var first = Grab(new Rectangle(region.Location, shown));
            // What the bug recorded: the screen's top-left corner, as large as the video frame.
            var corner = Grab(new Rectangle(primary.Location, full ? new Size(ow, oh) : shown), shown);
            if (!full)
            {
                service.UpdateRegion(moved.X, moved.Y, moved.Width, moved.Height);
                Thread.Sleep(1500);
            }
            var last = Grab(new Rectangle(moved.Location, shown));
            var firstNow = Grab(new Rectangle(region.Location, shown));
            service.Stop();
            if (!finished.Wait(TimeSpan.FromSeconds(30))) return new List<string> { $"{name}: never finished" };
            if (error != null) return new List<string> { $"{name}: {error}" };
            RecordingController.WaitUntilReleasedAsync(done!, 60_000).GetAwaiter().GetResult();

            Check(name + " start", Frame(done!, "-ss", "1.0", shown), first, corner, failures);
            if (!full) Check(name + " moved", Frame(done!, "-sseof", "-0.2", shown), last, firstNow, failures);
        }
        finally
        {
            foreach (var f in new[] { done, service.TempPath })
                if (f != null && File.Exists(f)) try { File.Delete(f); } catch { }
        }
        return failures;
    }

    // The frame must look like the region, and clearly more than like the other screen area.
    private void Check(string name, byte[] frame, byte[] expected, byte[] other, List<string> failures)
    {
        double toExpected = Difference(frame, expected), toOther = Difference(frame, other), apart = Difference(expected, other);
        output.WriteLine($"{name}: diff to region {toExpected:F1}, to the other area {toOther:F1} (areas differ by {apart:F1})");
        if (apart < 3) output.WriteLine($"{name}: screen areas look alike; inconclusive");
        else if (toExpected > toOther * 0.8) failures.Add($"{name}: frame is not the region (diff {toExpected:F1} vs {toOther:F1})");
    }

    private static double Difference(byte[] a, byte[] b)
    {
        long sum = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) sum += Math.Abs(a[i] - b[i]);
        return n == 0 ? 255 : (double)sum / n;
    }

    private static byte[] Grab(Rectangle r, Size? scaledTo = null)
    {
        var size = scaledTo ?? r.Size;
        using var shot = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(r.Location, Point.Empty, r.Size);
        using var bmp = new Bitmap(shot, size);
        var data = bmp.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[size.Width * size.Height * 4];
        for (int y = 0; y < size.Height; y++)
            Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * size.Width * 4, size.Width * 4);
        bmp.UnlockBits(data);
        return bytes;
    }

    private static byte[] Frame(string file, string seekFlag, string seek, Size size)
    {
        var psi = new ProcessStartInfo(FFmpegService.Instance.ExePath) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-v", "error", seekFlag, seek, "-i", file, "-frames:v", "1", "-vf", $"scale={size.Width}:{size.Height}", "-pix_fmt", "bgra", "-f", "rawvideo", "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        return ms.ToArray();
    }
}
