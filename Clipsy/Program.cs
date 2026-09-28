using System;
using System.Diagnostics;
using System.Threading;
using Clipsy.Services;

namespace Clipsy;

/// <summary>Custom entry point (replaces the XAML-generated Main) enforcing a
/// single running instance per user session before XAML initializes.</summary>
public static class Program
{
    // Local\ scope is per-session, preventing collisions across RDP / fast-user-switch.
    private const string MutexName = "Local\\Clipsy.SingleInstance.v1";
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(20);

    [STAThread]
    public static int Main(string[] args)
    {
        if (ProcessWatchdog.TryRunWatchdog(args, out int watchdogExitCode))
            return watchdogExitCode;

        if (CliService.TryRun(args, out int cliExitCode))
            return cliExitCode;

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew && !TakeOver(mutex))
            return 0;

        // Pipe answers PING before XAML is up, so a relaunch during a slow start hands off instead of killing us.
        SingleInstanceService.StartServer();
        ProcessWatchdog.StartForCurrentProcess();

        // Install native crash capture before XAML init so a fail-fast / AV
        // leaves a minidump + breadcrumb instead of vanishing silently.
        CrashHandler.Install();

        try
        {
            global::WinRT.ComWrappersSupport.InitializeComWrappers();
            global::Microsoft.UI.Xaml.Application.Start((p) =>
            {
                var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                new App();
            });
            return 0;
        }
        finally
        {
            SingleInstanceService.StopServer();
            try { mutex.ReleaseMutex(); } catch { /* process exiting */ }
        }
    }

    /// <summary>Returns true once this process owns the mutex; false after handing off to a live instance.</summary>
    private static bool TakeOver(Mutex mutex)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            switch (SingleInstanceService.Probe(out int pid))
            {
                case SingleInstanceService.ProbeResult.Alive:
                    SingleInstanceService.TrySendRequest(CliService.SerializeRequest("activate", Array.Empty<string>()), out _);
                    return false;
                case SingleInstanceService.ProbeResult.Hung:
                    Diagnostics.Log($"Existing instance pid={pid} is hung; replacing it.");
                    SingleInstanceService.KillInstance(pid);
                    return AcquireMutex(mutex, TimeSpan.FromSeconds(5));
                default:
                    // Starting up or shutting down: give it time before assuming it's wedged.
                    if (AcquireMutex(mutex, TimeSpan.FromMilliseconds(500))) return true;
                    if (sw.Elapsed < StartupGrace) continue;
                    Diagnostics.Log("Existing instance never answered; replacing it.");
                    SingleInstanceService.KillStaleInstances();
                    return AcquireMutex(mutex, TimeSpan.FromSeconds(5));
            }
        }
    }

    private static bool AcquireMutex(Mutex mutex, TimeSpan timeout)
    {
        try { return mutex.WaitOne(timeout); }
        catch (AbandonedMutexException) { return true; }
    }
}
