using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

public class WindowRecordingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task App_audio_is_added_to_a_recording(bool videoHasAudio)
    {
        var ffmpeg = FFmpegService.Instance;
        if (!ffmpeg.IsAvailable) return;
        var dir = Directory.CreateTempSubdirectory("clipsy_mux_").FullName;
        try
        {
            var video = Path.Combine(dir, "v.mp4");
            var args = new[] { "-f", "lavfi", "-i", "testsrc=size=320x240:rate=30:duration=2" }.ToList();
            if (videoHasAudio) args.AddRange(new[] { "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo", "-shortest", "-c:a", "aac" });
            args.AddRange(new[] { "-c:v", "libx264", "-pix_fmt", "yuv420p", "-y", video });
            Assert.True(await ffmpeg.RunAsync(args));

            var pcm = Path.Combine(dir, "a.pcm");
            var samples = new byte[ProcessAudioCapture.SampleRate * 4]; // 1 s, shorter than the video
            for (int i = 0; i < samples.Length; i += 2) BitConverter.TryWriteBytes(samples.AsSpan(i), (short)(i % 2000));
            File.WriteAllBytes(pcm, samples);

            var output = Path.Combine(dir, "out.mp4");
            Assert.True(await ffmpeg.AddAppAudioAsync(video, pcm, videoHasAudio, output));

            var info = Probe(output);
            Assert.Contains("Video: h264", info);
            Assert.Contains("Audio: aac", info);
            var duration = System.Text.RegularExpressions.Regex.Match(info, @"Duration: 00:00:(\d+\.\d+)");
            Assert.True(duration.Success && double.Parse(duration.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 1.9,
                "audio must be padded to the video's length: " + duration.Value);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // Records a real window for two seconds: no UI, no sound, but it touches the GPU encoder, so opt-in.
    [Fact]
    public void Records_a_window_with_app_audio()
    {
        if (Environment.GetEnvironmentVariable("CLIPSY_RECORD_TEST") != "1") return;
        var target = WindowTargets.VisibleInZOrder().First();
        using var service = new RecordingService();
        string? done = null, failed = null;
        var finished = new ManualResetEventSlim();
        service.RecordingComplete += p => { done = p; finished.Set(); };
        service.RecordingFailed += e => { failed = e; finished.Set(); };
        service.StartWindow(target.Hwnd, target.Bounds.Width & ~1, target.Bounds.Height & ~1, Environment.ProcessId);
        try
        {
            Thread.Sleep(2000);
            service.Stop();
            Assert.True(finished.Wait(TimeSpan.FromSeconds(30)), "recorder never finished");
            Assert.Null(failed);
            Assert.True(File.Exists(done) && RecordingController.Mp4HasIndex(done!), "no playable recording");
            Assert.False(service.AppAudioFailed);
            Assert.True(new FileInfo(service.AppAudioPath!).Length > ProcessAudioCapture.SampleRate * 4, "app audio timeline missing");
        }
        finally
        {
            foreach (var f in new[] { done, service.TempPath, service.AppAudioPath })
                if (f != null && File.Exists(f)) File.Delete(f);
        }
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
