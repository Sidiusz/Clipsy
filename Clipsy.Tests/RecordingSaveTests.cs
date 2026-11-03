using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

public class RecordingSaveTests
{
    [Fact]
    public async Task Save_waits_for_the_recorder_to_release_its_file()
    {
        var path = Path.GetTempFileName();
        try
        {
            var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            _ = Task.Delay(400).ContinueWith(_ => held.Dispose());
            var sw = Stopwatch.StartNew();
            Assert.True(await RecordingController.WaitUntilReleasedAsync(path, 10_000));
            Assert.InRange(sw.ElapsedMilliseconds, 300, 5_000);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Save_gives_up_on_a_file_that_stays_locked()
    {
        var path = Path.GetTempFileName();
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Assert.False(await RecordingController.WaitUntilReleasedAsync(path, 300));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Missing_file_is_not_waited_for()
    {
        var sw = Stopwatch.StartNew();
        Assert.False(await RecordingController.WaitUntilReleasedAsync(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4"), 10_000));
        Assert.True(sw.ElapsedMilliseconds < 1_000);
    }

    // The overlay drops its boost shortly after it appears; a recording started from it must keep its own.
    [Fact]
    public void Recording_keeps_the_priority_boost_until_it_ends()
    {
        CapturePriority.BeginRecording();
        try
        {
            Assert.True(CapturePriority.IsBoosted);
            CapturePriority.Restore();
            Assert.True(CapturePriority.IsBoosted);
        }
        finally { CapturePriority.EndRecording(); }
        Assert.False(CapturePriority.IsBoosted);
    }
}
