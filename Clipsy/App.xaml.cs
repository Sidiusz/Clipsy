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
            Diagnostics.Log("App.UnhandledException", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Diagnostics.Log("AppDomain.UnhandledException", ex);
            else
                Diagnostics.Log($"AppDomain.UnhandledException (non-Exception): {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Diagnostics.Log("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
        try
        {
            DebugSettings.IsBindingTracingEnabled = true;
            DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
            DebugSettings.BindingFailed += (_, ev) =>
                Diagnostics.Log($"BindingFailed: {ev.Message}");
            DebugSettings.XamlResourceReferenceFailed += (_, ev) =>
                Diagnostics.Log($"XamlResourceReferenceFailed: {ev.Message}");
        }
        catch (Exception ex)
        {
            Diagnostics.Log("DebugSettings setup failed", ex);
        }
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
        ThemeService.Register(HostWindow.Content as Microsoft.UI.Xaml.FrameworkElement);

        Hotkey = new HotkeyService(HostWindow.DispatcherQueue);
        RegisterHotkeys();
        SettingsService.Instance.SettingsChanged += OnSettingsChangedRewireHotkeys;

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
        if (RecordingController.IsRecording)
        {
            RecordingController.Current?.StopFromHotkey();
            return;
        }
        CaptureOverlayHost.ShowOverlay();
    }

    private void OnSettingsRequested()
    {
        // Defer past tray's nested TrackPopupMenu message pump.
        // Creating a WinUI Window directly inside that nested pump
        // makes XBF init NRE inside LoadComponent.
        var dq = HostWindow?.DispatcherQueue;
        if (dq != null)
            dq.TryEnqueue(() => Clipsy.Views.Settings.SettingsWindow.ShowOrActivate());
        else
            Clipsy.Views.Settings.SettingsWindow.ShowOrActivate();
    }

    private void RegisterHotkeys()
    {
        var s = SettingsService.Instance.Settings;
        string capture = string.IsNullOrWhiteSpace(s.HotkeyCapture) ? "Snapshot" : s.HotkeyCapture;
        string? record = string.IsNullOrWhiteSpace(s.HotkeyRecordSilentSave) ? null : s.HotkeyRecordSilentSave;
        bool ok = Hotkey!.Register(OnCaptureRequested, capture, OnRecordStopRequested, record);
        if (!ok)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Capture hotkey '{capture}' not registered. Win11 Snipping Tool may own PrintScreen.");
        }
    }

    private void OnSettingsChangedRewireHotkeys()
    {
        var s = SettingsService.Instance.Settings;
        string capture = string.IsNullOrWhiteSpace(s.HotkeyCapture) ? "Snapshot" : s.HotkeyCapture;
        string? record = string.IsNullOrWhiteSpace(s.HotkeyRecordSilentSave) ? null : s.HotkeyRecordSilentSave;
        Hotkey?.Reregister(capture, record);
    }

    private void OnRecordStopRequested()
    {
        if (RecordingController.IsRecording)
            RecordingController.Current?.StopFromHotkey();
    }

    private void OnExitRequested()
    {
        SettingsService.Instance.SettingsChanged -= OnSettingsChangedRewireHotkeys;
        Hotkey?.Dispose();
        Application.Current.Exit();
    }
}
