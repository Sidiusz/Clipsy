using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

public class SingleInstanceTests
{
    [Fact]
    public async Task ServerAnswersPingAndRequests()
    {
        SingleInstanceService.PipeName = "Clipsy.Tests." + Guid.NewGuid().ToString("N");
        SingleInstanceService.SetRequestHandler(req => "echo:" + req);
        SingleInstanceService.StartServer();

        Assert.Equal(SingleInstanceService.ProbeResult.Alive, SingleInstanceService.Probe(out int pid));
        Assert.Equal(Environment.ProcessId, pid);
        Assert.True(SingleInstanceService.TrySendRequest("hello", out var response));
        Assert.Equal("echo:hello", response);

        // Several concurrent clients must all be served.
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(() =>
            SingleInstanceService.TrySendRequest("r" + i, out var r) ? r : null)));
        for (int i = 0; i < results.Length; i++) Assert.Equal("echo:r" + i, results[i]);

        // A recording instance answers BUSY, which a second launch must treat as alive.
        SingleInstanceService.SetBusyCheck(() => true);
        Assert.Equal(SingleInstanceService.ProbeResult.Busy, SingleInstanceService.Probe(out _));
        Assert.True(SingleInstanceService.TryPingExisting());
        SingleInstanceService.SetBusyCheck(null);
        Assert.Equal(SingleInstanceService.ProbeResult.Alive, SingleInstanceService.Probe(out _));
    }
}
