using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Clipsy.Localization;
using Clipsy.Services;
using Clipsy.Views;

namespace Clipsy;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public MainWindow? HostWindow { get; private set; }
    public HotkeyService? Hotkey { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Unhandled: {e.Exception}");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Strings.Initialize();
        HostWindow = new MainWindow();
        HostWindow.CaptureRequested += OnCaptureRequested;
        HostWindow.SettingsRequested += OnSettingsRequested;
        HostWindow.ExitRequested += OnExitRequested;

        // Activate the host so the WinUI 3 XAML island starts. The window
        // is offscreen + tool-window so it's invisible, but it must be
        // active or the TaskbarIcon's commands and ContextFlyout never wire.
        HostWindow.Activate();

        Hotkey = new HotkeyService(HostWindow.DispatcherQueue);
        bool ok = Hotkey.RegisterDefault(OnCaptureRequested);
        if (!ok)
        {
            System.Diagnostics.Debug.WriteLine("[Clipsy] PrintScreen hotkey not registered. Likely Windows is intercepting it (Snipping Tool override).");
        }

        _ = CheckUpdatesIfDueAsync();
    }

    public async Task CheckUpdatesIfDueAsync(bool force = false)
    {
        try
        {
            var s = SettingsService.Instance.Settings;
            if (!force)
            {
                if (s.UpdateInterval == "never") return;
                if (!UpdateService.ShouldCheckNow(s.UpdateInterval, s.LastUpdateCheckUtc)) return;
            }
            var info = await UpdateService.CheckLatestAsync();
            s.LastUpdateCheckUtc = DateTime.UtcNow;
            SettingsService.Instance.Save();
            if (info == null)
            {
                if (force) NotificationService.Warning("UpdateCheckFailed");
                return;
            }
            if (!UpdateService.IsNewer(info.Version, UpdateService.CurrentVersion()))
            {
                if (force) NotificationService.Info("UpdateUpToDate");
                return;
            }
            if (!force && info.Version == s.SkippedVersion) return;
            NotificationService.InfoText($"Clipsy {info.Version}", Strings.Get("UpdateAvailable"));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Update check pipeline failed: {ex.Message}");
        }
    }

    private void OnCaptureRequested()
    {
        CaptureOverlayHost.ShowOverlay();
    }

    private void OnSettingsRequested()
    {
        Clipsy.Views.Settings.SettingsWindow.ShowOrActivate();
    }

    private void OnExitRequested()
    {
        Hotkey?.Dispose();
        Application.Current.Exit();
    }
}
