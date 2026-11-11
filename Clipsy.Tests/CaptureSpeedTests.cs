using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Clipsy.Services;
using Xunit;
using Xunit.Abstractions;

namespace Clipsy.Tests;

// Times the screen capture the overlay opens with, as a normal process and in Windows efficiency mode
// (Idle class + CPU throttling, what the tray icon library used to switch on). No UI; opt-in.
// CLIPSY_PERF_TEST=load also saturates every core for a few seconds, like a game would.
[Collection("Recording")]
public class CaptureSpeedTests(ITestOutputHelper output)
{
    [Fact]
    public void Screen_capture_timings()
    {
        var mode = Environment.GetEnvironmentVariable("CLIPSY_PERF_TEST");
        if (mode is not ("1" or "load")) return;
        var freeze = new ScreenFreezeService();
        freeze.Capture(includeCursor: false); // device creation and JIT
        long normal = Measure(freeze, "normal, idle PC", 12);
        Assert.True(normal < 2000, $"capture takes {normal} ms");
        if (mode != "load") return;

        var load = new List<Process>();
        try
        {
            for (int i = 0; i < Environment.ProcessorCount; i++)
                load.Add(Process.Start(new ProcessStartInfo("powershell.exe",
                    "-NoProfile -Command \"$e=(Get-Date).AddSeconds(70); while((Get-Date) -lt $e){}\"") { CreateNoWindow = true, UseShellExecute = false })!);
            Thread.Sleep(4000); // let them all spin up
            Measure(freeze, "normal, all cores busy", 6);
            Measure(freeze, "boosted like a hotkey capture, all cores busy", 6, boost: true);
            SetEfficiencyMode(true);
            Measure(freeze, "efficiency mode, all cores busy", 1);
        }
        finally
        {
            SetEfficiencyMode(false);
            foreach (var p in load) try { p.Kill(); p.Dispose(); } catch { }
        }
    }

    private long Measure(ScreenFreezeService freeze, string label, int count, bool boost = false)
    {
        var times = new long[count];
        for (int i = 0; i < count; i++)
        {
            Thread.Sleep(300); // a capture thread that just woke up, as after a hotkey
            var sw = Stopwatch.StartNew();
            if (boost) CapturePriority.Boost();
            try { freeze.Capture(includeCursor: false); }
            finally { if (boost) CapturePriority.Restore(); }
            times[i] = sw.ElapsedMilliseconds;
        }
        output.WriteLine($"PERF {label}: {string.Join(" ", times)} ms ({ModernScreenCaptureService.LastStages})");
        Array.Sort(times);
        return times[count / 2];
    }

    private static void SetEfficiencyMode(bool on)
    {
        var self = GetCurrentProcess();
        var state = new POWER_THROTTLING { Version = 1, ControlMask = 1, StateMask = on ? 1u : 0u };
        SetProcessInformation(self, 4, ref state, 12);
        SetPriorityClass(self, on ? 0x40u : 0x20u);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POWER_THROTTLING { public uint Version, ControlMask, StateMask; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] private static extern bool SetPriorityClass(IntPtr process, uint cls);
    [DllImport("kernel32.dll")] private static extern bool SetProcessInformation(IntPtr process, int cls, ref POWER_THROTTLING info, int size);
}
