using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Clipsy.Services;
using Xunit;
using Xunit.Abstractions;

namespace Clipsy.Tests;

// Records the primary monitor with every output setting through the real RecordingService.
// Settings change in memory only. No UI, no sound, but GPU encoding: opt-in.
[Collection("Recording")] // one recorder at a time: they share the settings and the GPU encoder
public class RecordingMatrixTests(ITestOutputHelper output)
{
    [Fact]
    public void Every_resolution_framerate_codec_and_capture_api_records_the_whole_region()
    {
        if (Environment.GetEnvironmentVariable("CLIPSY_RECORD_TEST") != "1" || !FFmpegService.Instance.IsAvailable) return;
        var settings = SettingsService.Instance.Settings;
        var saved = settings.Clone();
        var primary = ScreenFreezeService.EnumerateMonitors().First(m => m.IsPrimary).Bounds;
        var failures = new System.Collections.Generic.List<string>();
        try
        {
            settings.MicrophoneEnabled = false;
            foreach (var api in new[] { false, true })
            foreach (var codec in new[] { "H.264", "H.265" })
            foreach (var fps in new[] { 30, 60 })
            foreach (var res in new[] { "Original", "1440p", "1080p", "720p", "480p" })
            {
                settings.VideoCodec = codec;
                settings.VideoFramerate = fps;
                settings.VideoResolution = res;
                var (ew, eh) = RecordingService.ResolveOutputSize(res, primary.Width, primary.Height);
                string name = $"{(api ? "DDA" : "WGC")} {codec} {fps}fps {res}";
                using var service = new RecordingService();
                string? done = null, failed = null;
                var finished = new ManualResetEventSlim();
                service.RecordingComplete += p => { done = p; finished.Set(); };
                service.RecordingFailed += e => { failed = e; finished.Set(); };
                try
                {
                    service.Start(primary.X, primary.Y, primary.Width, primary.Height, desktopDuplication: api);
                    Thread.Sleep(1200);
                    service.Stop();
                    if (!finished.Wait(TimeSpan.FromSeconds(30))) { failures.Add($"{name}: never finished"); continue; }
                    if (failed != null) { failures.Add($"{name}: {failed}"); continue; }
                    bool released = RecordingController.WaitUntilReleasedAsync(done!, 60_000).GetAwaiter().GetResult();
                    if (!released) output.WriteLine($"{name}: file still locked after 60 s");
                    var info = Probe(done!);
                    var m = Regex.Match(info, @"Video: (\w+).*?, (\d+)x(\d+)");
                    string got = m.Success ? $"{m.Groups[1].Value} {m.Groups[2].Value}x{m.Groups[3].Value}" : "no video";
                    output.WriteLine($"{name}: {got}");
                    if (!m.Success) output.WriteLine(info.Length > 400 ? info[^400..] : info);
                    if (got != $"{(codec == "H.265" ? "hevc" : "h264")} {ew}x{eh}") failures.Add($"{name}: expected {ew}x{eh}, got {got}");
                    if (!info.Contains($", {fps} fps,")) failures.Add($"{name}: frame rate isn't declared as {fps}");
                }
                finally
                {
                    foreach (var f in new[] { done, service.TempPath })
                        if (f != null && File.Exists(f)) try { File.Delete(f); } catch { }
                }
            }
        }
        finally
        {
            settings.MicrophoneEnabled = saved.MicrophoneEnabled;
            settings.VideoCodec = saved.VideoCodec;
            settings.VideoFramerate = saved.VideoFramerate;
            settings.VideoResolution = saved.VideoResolution;
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static string Probe(string file)
    {
        var psi = new ProcessStartInfo(FFmpegService.Instance.ExePath) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(file);
        using var p = Process.Start(psi)!;
        var text = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return text;
    }
}
