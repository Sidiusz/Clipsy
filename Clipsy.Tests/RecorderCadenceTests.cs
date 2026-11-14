using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using Clipsy.Services;
using ScreenRecorderLib;
using Xunit;
using Xunit.Abstractions;

namespace Clipsy.Tests;

[Collection("Recording")]
public class RecorderCadenceTests(ITestOutputHelper output)
{
    // A static source exposes resume stalls without opening desktop windows.
    [Fact]
    public async Task Paused_source_resumes_without_waiting_for_content_changes()
    {
        if (Environment.GetEnvironmentVariable("CLIPSY_RECORD_TEST") != "1" || !FFmpegService.Instance.IsAvailable) return;
        var directory = Directory.CreateTempSubdirectory("clipsy_cadence_").FullName;
        try
        {
            CapturePriority.BeginRecording();
            using var timing = new RecordingTimerResolution();
            var imagePath = Path.Combine(directory, "source.png");
            using (var bitmap = new Bitmap(320, 180))
            {
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.Navy);
                graphics.FillRectangle(Brushes.Orange, 40, 30, 150, 100);
                bitmap.Save(imagePath, System.Drawing.Imaging.ImageFormat.Png);
            }
            using var recorder = Recorder.CreateRecorder(new RecorderOptions
            {
                SourceOptions = new SourceOptions { RecordingSources = new List<RecordingSourceBase> { new ImageRecordingSource(imagePath) } },
                OutputOptions = new OutputOptions { RecorderMode = RecorderMode.Video, OutputFrameSize = new ScreenSize(1920, 1080) },
                VideoEncoderOptions = new VideoEncoderOptions
                {
                    Encoder = new H264VideoEncoder { BitrateMode = H264BitrateControlMode.UnconstrainedVBR, EncoderProfile = H264Profile.High },
                    Framerate = 60,
                    Bitrate = 8_000_000,
                    IsFixedFramerate = true,
                    IsHardwareEncodingEnabled = true,
                },
                AudioOptions = new AudioOptions { IsAudioEnabled = false },
                MouseOptions = new MouseOptions { IsMousePointerEnabled = false, IsMouseClicksDetected = false },
            });
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            long frames = 0;
            recorder.OnFrameRecorded += (_, _) => Interlocked.Increment(ref frames);
            recorder.OnStatusChanged += (_, e) => { if (e.Status == RecorderStatus.Recording) started.TrySetResult(); };
            recorder.OnRecordingComplete += (_, e) => finished.TrySetResult(e.FilePath);
            recorder.OnRecordingFailed += (_, e) =>
            {
                var error = new InvalidOperationException(e.Error);
                started.TrySetException(error);
                finished.TrySetException(error);
            };
            var path = Path.Combine(directory, "cadence.mp4");
            recorder.Record(path);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            double beforePause = await MeasureCadence();
            recorder.Pause();
            await Task.Delay(100);
            long pausedFrames = Interlocked.Read(ref frames);
            await Task.Delay(750);
            Assert.InRange(Interlocked.Read(ref frames) - pausedFrames, 0, 1);
            recorder.Resume();
            double afterResume = await MeasureCadence();
            Assert.True(afterResume >= beforePause * 0.75, $"Resume reduced encoder cadence from {beforePause:0.00} to {afterResume:0.00} fps");
            recorder.Stop();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(await RecordingController.WaitUntilReleasedAsync(path, 10_000));

            var probe = new ProcessStartInfo(FFmpegService.Instance.ExePath) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-hide_banner", "-i", path }) probe.ArgumentList.Add(argument);
            using var process = Process.Start(probe)!;
            string info = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var duration = Regex.Match(info, @"Duration: 00:00:(\d+\.\d+)");
            Assert.True(duration.Success, info);
            Assert.InRange(double.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture), 5.8, 6.6);
            Assert.Contains("60 fps", info);

            async Task<double> MeasureCadence()
            {
                long before = Interlocked.Read(ref frames);
                var clock = Stopwatch.StartNew();
                await Task.Delay(3000);
                double fps = (Interlocked.Read(ref frames) - before) / clock.Elapsed.TotalSeconds;
                output.WriteLine($"Native recorder submitted {fps:0.00} fps");
                Assert.InRange(fps, 30, 61.5);
                return fps;
            }
        }
        finally
        {
            CapturePriority.EndRecording();
            Directory.Delete(directory, recursive: true);
        }
    }
}
