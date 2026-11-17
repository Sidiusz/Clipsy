using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

public class HotkeyRecoveryTests
{
    [Fact]
    public void MissedReleaseDoesNotDisableLaterPresses()
    {
        var state = new HotkeyPressState();
        Assert.True(state.TryPress(0x2C));
        Assert.False(state.TryPress(0x2C));

        // Windows dropped the hook before key-up; the recovery tick sees the key is up.
        state.ReleaseIfUp(physicallyDown: false);
        Assert.Equal(0u, state.HeldKey);
        Assert.True(state.TryPress(0x2C));
    }

    [Fact]
    public void RecoveryDoesNotRetriggerAHeldKey()
    {
        var state = new HotkeyPressState();
        Assert.True(state.TryPress(0x2C));
        state.ReleaseIfUp(physicallyDown: true);
        Assert.False(state.TryPress(0x2C));
        state.Release(0x41);
        Assert.False(state.TryPress(0x2C));
        state.Release(0x2C);
        Assert.True(state.TryPress(0x2C));
    }
}
