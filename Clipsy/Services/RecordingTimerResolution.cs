using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Clipsy.Services;

// ScreenRecorderLib uses short native waits to pace frames. The default Windows timer
// can round those waits to 15.6 ms, leaving a 60 fps file full of repeated frames.
internal sealed class RecordingTimerResolution : IDisposable
{
    private const uint IgnoreTimerResolution = 4;
    private const int ProcessPowerThrottling = 4;
    private readonly bool _timerRequested;
    private readonly PowerThrottlingState? _previousPowerState;
    private int _disposed;

    public RecordingTimerResolution()
    {
        // Windows 11 can ignore timer requests from an occluded recording app.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            if (TryGetPowerState(out var state))
            {
                _previousPowerState = state;
                state.ControlMask |= IgnoreTimerResolution;
                state.StateMask &= ~IgnoreTimerResolution;
                if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>()))
                {
                    _previousPowerState = null;
                    Diagnostics.Log($"Recording timer policy failed: {Marshal.GetLastWin32Error()}");
                }
            }
            else Diagnostics.Log($"Reading recording timer policy failed: {Marshal.GetLastWin32Error()}");
        }

        uint result = timeBeginPeriod(1);
        _timerRequested = result == 0;
        if (!_timerRequested) Diagnostics.Log($"Recording timer resolution failed: {result}");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_timerRequested) timeEndPeriod(1);
        if (_previousPowerState is { } previous &&
            TryGetPowerState(out var state))
        {
            state.ControlMask = (state.ControlMask & ~IgnoreTimerResolution) | (previous.ControlMask & IgnoreTimerResolution);
            state.StateMask = (state.StateMask & ~IgnoreTimerResolution) | (previous.StateMask & IgnoreTimerResolution);
            if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>()))
                Diagnostics.Log($"Restoring recording timer policy failed: {Marshal.GetLastWin32Error()}");
        }
    }

    private static bool TryGetPowerState(out PowerThrottlingState state)
    {
        state = new PowerThrottlingState { Version = 1 };
        return GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<PowerThrottlingState>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState state, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState state, int size);
}
