using System;
using System.Threading;
using Clipsy.Services;

namespace Clipsy;

/// <summary>Custom entry point (replaces the XAML-generated Main) enforcing a
/// single running instance per user session before XAML initializes.</summary>
public static class Program
{
    // Local\ scope is per-session, preventing collisions across RDP / fast-user-switch.
    private const string MutexName = "Local\\Clipsy.SingleInstance.v1";

    [STAThread]
    public static int Main(string[] args)
    {
        bool createdNew;
        // Created-new is the authoritative signal; it also covers an abandoned
        // mutex left by a crashed previous instance.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        if (!createdNew)
        {
            // Live instance holds the mutex: hand off if it answers the ping,
            // else it's hung — kill it and take over so relaunch isn't bricked.
            if (SingleInstanceService.TryPingExisting())
                return 0;
            SingleInstanceService.KillStaleInstances();
            try { mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { /* previous owner died — now ours */ }
        }

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
            try { mutex.ReleaseMutex(); } catch { /* ignore — process exiting */ }
        }
    }

}
