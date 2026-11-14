using System.Runtime.InteropServices;
using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

[Collection("Recording")]
public class RecordingTimingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Timer_policy_is_honored_during_recording_and_restored_afterward(bool ignoreTimers)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        Assert.True(TryGetPowerState(out var original));
        var requested = original;
        requested.ControlMask |= 4u;
        requested.StateMask = ignoreTimers ? requested.StateMask | 4u : requested.StateMask & ~4u;
        Assert.True(SetProcessInformation(GetCurrentProcess(), 4, ref requested, 12));
        try { CheckTimerPolicy(); }
        finally { Assert.True(SetProcessInformation(GetCurrentProcess(), 4, ref original, 12)); }
    }

    private static void CheckTimerPolicy()
    {
        Assert.True(TryGetPowerState(out var before));
        var timer = new RecordingTimerResolution();
        try
        {
            Assert.True(TryGetPowerState(out var during));
            Assert.Equal(4u, during.ControlMask & 4u);
            Assert.Equal(0u, during.StateMask & 4u);
            Assert.Equal(before.ControlMask & ~4u, during.ControlMask & ~4u);
            Assert.Equal(before.StateMask & ~4u, during.StateMask & ~4u);
        }
        finally { timer.Dispose(); }

        Assert.True(TryGetPowerState(out var after));
        Assert.Equal(before.ControlMask, after.ControlMask);
        Assert.Equal(before.StateMask, after.StateMask);
        timer.Dispose();
        Assert.True(TryGetPowerState(out var twice));
        Assert.Equal(after.ControlMask, twice.ControlMask);
        Assert.Equal(after.StateMask, twice.StateMask);
    }

    private static bool TryGetPowerState(out PowerState state)
    {
        state = new PowerState { Version = 1 };
        return GetProcessInformation(GetCurrentProcess(), 4, ref state, 12);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerState { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(IntPtr process, int infoClass, ref PowerState state, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerState state, int size);
}
