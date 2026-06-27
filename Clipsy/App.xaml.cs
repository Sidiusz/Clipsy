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

    private Clipsy.Views.TrayMenuWindow? _trayMenu;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _updateTimer;

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
#if DEBUG
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
#endif
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Strings.Initialize();
        HostWindow = new MainWindow();
        HostWindow.CaptureRequested += OnCaptureRequested;
        HostWindow.MenuRequested    += OnMenuRequested;

        // Activate the host to start the XAML island; offscreen tool-window so
        // invisible, but must be active or the TaskbarIcon commands never wire.
        HostWindow.Activate();

        // Pre-create the tray menu after the XAML island is live.
        _trayMenu = new Clipsy.Views.TrayMenuWindow();
        _trayMenu.CaptureClicked              += OnCaptureRequested;
        _trayMenu.OpenScreenshotsFolderClicked+= OnOpenFolderRequested;
        _trayMenu.OpenVideoFolderClicked      += OnOpenVideoFolderRequested;
        _trayMenu.SettingsClicked             += OnSettingsRequested;
        _trayMenu.UpdateStatusClicked         += OnSettingsRequested;
        _trayMenu.ExitClicked                 += OnExitRequested;
        ThemeService.Register(HostWindow.Content as Microsoft.UI.Xaml.FrameworkElement);

        SingleInstanceService.StartServer();

        Hotkey = new HotkeyService(HostWindow.DispatcherQueue);
        RegisterHotkeys();
        SettingsService.Instance.SettingsChanged += OnSettingsChangedRewireHotkeys;

        // Pre-create + warm the Settings window off-screen so the first open is
        // instant and never shows WinUI's black first-paint frame.
        Clipsy.Views.Settings.SettingsWindow.Prewarm();

        _ = CheckUpdatesIfDueAsync();
        StartUpdateTimer();

        // Warm the capture pipeline off-thread so the first PrintScreen doesn't
        // pay JIT + XAML cold init + first BitBlt at once.
        System.Threading.Tasks.Task.Run(WarmupCapturePath);
    }

    private static void WarmupCapturePath()
    {
        try
        {
            // Touch the freeze service (loads GDI handles, screen-bounds calc).
            _ = Services.ScreenFreezeService.GetVirtualScreenBounds();
            // Force-load the overlay window type so its XAML resources are
            // parsed/JITted before the user presses PrintScreen the first time.
            _ = typeof(Views.CaptureOverlayWindow).Assembly;
            _ = typeof(Views.Recording.RecordingHudWindow).Assembly;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Warmup failed: {ex.Message}");
        }
    }

    // Periodic re-check so a long-running tray instance finds updates without a
    // restart; CheckUpdatesIfDueAsync itself gates on the chosen interval.
    private void StartUpdateTimer()
    {
        var dq = HostWindow?.DispatcherQueue;
        if (dq == null) return;
        _updateTimer = dq.CreateTimer();
        _updateTimer.Interval = TimeSpan.FromMinutes(30);
        _updateTimer.IsRepeating = true;
        _updateTimer.Tick += (_, _) => _ = CheckUpdatesIfDueAsync();
        _updateTimer.Start();
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

            _trayMenu?.SetUpdateStatus(Clipsy.Views.TrayUpdateStatus.Checking);

            var result = await UpdateService.CheckLatestAsync();
            s.LastUpdateCheckUtc = DateTime.UtcNow;
            SettingsService.Instance.Save();

            if (result.Status == UpdateCheckStatus.Failed)
            {
                if (force) NotificationService.Warning("UpdateCheckFailed");
                _trayMenu?.SetUpdateStatus(Clipsy.Views.TrayUpdateStatus.Failed);
                return;
            }
            var info = result.Info;
            if (info == null || !UpdateService.IsNewer(info.Version, UpdateService.CurrentVersion()))
            {
                if (force) NotificationService.Info("UpdateUpToDate");
                _trayMenu?.SetUpdateStatus(Clipsy.Views.TrayUpdateStatus.UpToDate);
                return;
            }
            if (!force && info.Version == s.SkippedVersion)
            {
                _trayMenu?.SetUpdateStatus(Clipsy.Views.TrayUpdateStatus.UpToDate);
                return;
            }
            var version = info.Version;
            NotificationService.UpdateAvailable(
                info,
                Strings.Get("UpdateAvailable"),
                skipVersion: () =>
                {
                    SettingsService.Instance.Settings.SkippedVersion = version;
                    SettingsService.Instance.Save();
                });
            _trayMenu?.SetUpdateStatus(Clipsy.Views.TrayUpdateStatus.Available, info.Version);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Update check pipeline failed: {ex.Message}");
            _trayMenu?.SetUpdateStatus(Clipsy.Views.TrayUpdateStatus.Failed);
        }
    }

    private void OnMenuRequested()
    {
        _trayMenu?.ShowAtCursor();
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
        // Defer past the tray's nested TrackPopupMenu pump: creating a Window
        // inside it makes XBF init NRE in LoadComponent.
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
        string? mic    = string.IsNullOrWhiteSpace(s.HotkeyMicToggle) ? null : s.HotkeyMicToggle;
        bool ok = Hotkey!.Register(OnCaptureRequested, capture, OnRecordStopRequested, record, OnMicToggleRequested, mic);
        if (!ok)
        {
            // Capture hotkey didn't register (another app owns it) — warn so the
            // user can rebind instead of failing silently.
            System.Diagnostics.Debug.WriteLine(
                $"[Clipsy] Capture hotkey '{capture}' not registered. Win11 Snipping Tool may own PrintScreen.");
            NotificationService.Warning("WarnHotkeyConflict");
        }
    }

    private void OnSettingsChangedRewireHotkeys()
    {
        var s = SettingsService.Instance.Settings;
        string capture = string.IsNullOrWhiteSpace(s.HotkeyCapture) ? "Snapshot" : s.HotkeyCapture;
        string? record = string.IsNullOrWhiteSpace(s.HotkeyRecordSilentSave) ? null : s.HotkeyRecordSilentSave;
        string? mic    = string.IsNullOrWhiteSpace(s.HotkeyMicToggle) ? null : s.HotkeyMicToggle;
        Hotkey?.Reregister(capture, record, mic);
    }

    private void OnRecordStopRequested()
    {
        if (RecordingController.IsRecording)
            RecordingController.Current?.StopFromHotkey();
    }

    private void OnMicToggleRequested()
    {
        RecordingController.Current?.ToggleMic();
    }

    private void OnExitRequested()
    {
        SettingsService.Instance.SettingsChanged -= OnSettingsChangedRewireHotkeys;
        Hotkey?.Dispose();
        HostWindow?.TrayIconControl.Dispose();
        _trayMenu?.Close();
        _trayMenu = null;
        Application.Current.Exit();
    }

    private void OnRecordRequested()
    {
        // Reuses the same capture overlay — user picks a region, then the
        // record button on the floating toolbar starts recording.
        if (RecordingController.IsRecording)
        {
            RecordingController.Current?.StopFromHotkey();
            return;
        }
        CaptureOverlayHost.ShowOverlay();
    }

    private void OnOpenVideoFolderRequested()
    {
        try
        {
            var s = SettingsService.Instance;
            var folder = string.IsNullOrEmpty(s.Settings.VideoFolder)
                ? s.DefaultVideoFolder
                : s.Settings.VideoFolder!;
            if (System.IO.Directory.Exists(folder))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] OpenVideoFolder failed: {ex.Message}");
        }
    }

    private void OnOpenFolderRequested()
    {
        try
        {
            var s = SettingsService.Instance;
            var folder = string.IsNullOrEmpty(s.Settings.ScreenshotFolder)
                ? s.DefaultScreenshotFolder
                : s.Settings.ScreenshotFolder!;
            if (System.IO.Directory.Exists(folder))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] OpenFolder failed: {ex.Message}");
        }
    }

    private void OnAboutRequested()
    {
        // Reuse Settings with the Info pane preselected.
        var dq = HostWindow?.DispatcherQueue;
        if (dq != null)
            dq.TryEnqueue(() => Clipsy.Views.Settings.SettingsWindow.ShowOrActivate());
        else
            Clipsy.Views.Settings.SettingsWindow.ShowOrActivate();
    }
}
