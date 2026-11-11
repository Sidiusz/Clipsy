using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Clipsy.Services;

/// <summary>Raises the process's CPU and GPU scheduling priority from the capture
/// hotkey until shortly after the overlay is on screen, and for a whole recording, so games
/// saturating the machine can't starve the capture, the overlay or the recorder.</summary>
internal static class CapturePriority
{
    private static readonly object _lock = new();
    private static bool _boosted;
    private static uint _savedCpuClass;
    private static bool _cpuRaised;
    private static int _savedGpuClass = -1;
    private static bool _gpuFailureLogged;
    // Bumped by every Boost, so a delayed restore never ends a later capture's boost.
    private static int _generation;
    private static Timer? _restoreTimer;
    private static bool _recordingHold;

    internal static bool IsBoosted { get { lock (_lock) return _boosted; } }

    /// <summary>Keeps the boost until <see cref="EndRecording"/>, whatever the overlay restores meanwhile.</summary>
    public static void BeginRecording()
    {
        lock (_lock)
        {
            _recordingHold = true;
            Boost();
        }
    }

    public static void EndRecording()
    {
        lock (_lock)
        {
            if (!_recordingHold) return;
            _recordingHold = false;
            Restore();
        }
    }

    /// <summary>Opts out of Windows efficiency mode (Idle priority class plus CPU throttling): a hotkey app
    /// in it waits seconds for CPU time under a game. Also stops Windows throttling the windowless process itself.</summary>
    public static void LeaveEfficiencyMode()
    {
        try
        {
            var self = GetCurrentProcess();
            var state = new PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask = POWER_THROTTLING_EXECUTION_SPEED, StateMask = 0 };
            if (!SetProcessInformation(self, ProcessPowerThrottling, ref state, Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
                Diagnostics.Log($"Disabling power throttling failed err=0x{Marshal.GetLastWin32Error():X}");
            uint cls = GetPriorityClass(self);
            if (cls is IDLE_PRIORITY_CLASS or BELOW_NORMAL_PRIORITY_CLASS)
            {
                SetPriorityClass(self, NORMAL_PRIORITY_CLASS);
                Diagnostics.Log($"Priority class was 0x{cls:X}; set to Normal.");
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { }
    }

    private const int ProcessPowerThrottling = 4;
    private const uint POWER_THROTTLING_EXECUTION_SPEED = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE { public uint Version, ControlMask, StateMask; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PROCESS_POWER_THROTTLING_STATE info, int size);

    public static void Boost()
    {
        lock (_lock)
        {
            if (_boosted) return;
            _boosted = true;
            _generation++;
            var self = GetCurrentProcess();

            _savedCpuClass = GetPriorityClass(self);
            // Leave a priority the user raised themselves (High/Realtime) alone.
            if (_savedCpuClass is NORMAL_PRIORITY_CLASS or ABOVE_NORMAL_PRIORITY_CLASS
                    or BELOW_NORMAL_PRIORITY_CLASS or IDLE_PRIORITY_CLASS)
            {
                _cpuRaised = SetPriorityClass(self, HIGH_PRIORITY_CLASS);
                if (!_cpuRaised)
                    Diagnostics.Log($"SetPriorityClass(HIGH) failed err=0x{Marshal.GetLastWin32Error():X}");
            }

            // GPU work (the overlay's composition) queues behind the games' frames otherwise.
            try
            {
                _savedGpuClass = D3DKMTGetProcessSchedulingPriorityClass(self, out int gpu) == 0 ? gpu : -1;
                if (_savedGpuClass is >= 0 and < GPU_PRIORITY_HIGH)
                {
                    int status = D3DKMTSetProcessSchedulingPriorityClass(self, GPU_PRIORITY_HIGH);
                    if (status != 0) LogGpuFailure($"status=0x{status:X8}");
                }
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                _savedGpuClass = -1;
                LogGpuFailure(ex.Message);
            }
        }
    }

    public static void Restore()
    {
        lock (_lock)
        {
            if (!_boosted || _recordingHold) return;
            _boosted = false;
            var self = GetCurrentProcess();
            if (_cpuRaised) SetPriorityClass(self, _savedCpuClass);
            _cpuRaised = false;
            if (_savedGpuClass is >= 0 and < GPU_PRIORITY_HIGH)
            {
                try { D3DKMTSetProcessSchedulingPriorityClass(self, _savedGpuClass); }
                catch { }
            }
        }
    }

    /// <summary>Restores after <paramref name="delayMs"/> unless a new boost started meanwhile.</summary>
    public static void RestoreAfter(int delayMs)
    {
        lock (_lock)
        {
            if (!_boosted) return;
            int generation = _generation;
            _restoreTimer?.Dispose();
            _restoreTimer = new Timer(_ =>
            {
                lock (_lock)
                {
                    if (generation == _generation) Restore();
                }
            }, null, delayMs, Timeout.Infinite);
        }
    }

    private static void LogGpuFailure(string detail)
    {
        if (_gpuFailureLogged) return;
        _gpuFailureLogged = true;
        Diagnostics.Log($"GPU scheduling priority boost unavailable: {detail}");
    }

    private const uint IDLE_PRIORITY_CLASS         = 0x00000040;
    private const uint BELOW_NORMAL_PRIORITY_CLASS = 0x00004000;
    private const uint NORMAL_PRIORITY_CLASS       = 0x00000020;
    private const uint ABOVE_NORMAL_PRIORITY_CLASS = 0x00008000;
    private const uint HIGH_PRIORITY_CLASS         = 0x00000080;

    // D3DKMT_SCHEDULINGPRIORITYCLASS: IDLE, BELOW_NORMAL, NORMAL, ABOVE_NORMAL, HIGH, REALTIME.
    private const int GPU_PRIORITY_HIGH = 4;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetPriorityClass(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr hProcess, uint dwPriorityClass);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTGetProcessSchedulingPriorityClass(IntPtr hProcess, out int priority);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTSetProcessSchedulingPriorityClass(IntPtr hProcess, int priority);
}
