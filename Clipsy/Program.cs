using System;
using System.Threading;

namespace Clipsy;

/// <summary>
/// Custom entry point. Replaces the XAML-generated Main (disabled via
/// DISABLE_XAML_GENERATED_MAIN in the csproj) so we can enforce a single
/// running instance per user session before XAML initializes.
/// </summary>
public static class Program
{
    // Per-user mutex name. Suffix with the user SID would be more correct,
    // but per-user is good enough — Local\ scope already prevents collisions
    // across different RDP / fast-user-switch sessions.
    private const string MutexName = "Local\\Clipsy.SingleInstance.v1";

    [STAThread]
    public static int Main(string[] args)
    {
        bool createdNew;
        // Created-new is the only authoritative signal. WaitOne(0) on an
        // existing mutex would also fail if a previous instance crashed
        // and left an abandoned mutex — createdNew handles that path.
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        if (!createdNew)
            return 0;

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
