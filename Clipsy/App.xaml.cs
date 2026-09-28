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

    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;
    private Clipsy.Views.TrayMenuWindow? _trayMenu;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _updateTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _idleTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _watchdogTimer;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Unhandled: {e.Exception}");
            Diagnostics.Log("App.UnhandledException", e.Exception);
            // Do not leave the process alive in a corrupted/zombie UI state.
            // The external watchdog restarts unexpected failures; clean exit is
            // signalled before intentional shutdown so it will not resurrect it.
            e.Handled = false;
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
        AutostartService.MigrateLegacyScheduledTaskInBackground();
        HostWindow = new MainWindow();
        _uiQueue = HostWindow.DispatcherQueue;
        HostWindow.CaptureRequested += OnCaptureRequested;
        HostWindow.MenuRequested    += OnMenuRequested;
        HostWindow.SessionEnding    += () => _uiQueue?.TryEnqueue(OnExitRequested);

        // Activate the host to start the XAML island; offscreen tool-window so
        // invisible, but must be active or the TaskbarIcon commands never wire.
        HostWindow.Activate();
        CaptureOverlayHost.Initialize(HostWindow.DispatcherQueue);
        StartWatchdogHeartbeat();

        // Pre-create the tray menu after the XAML island is live.
        _trayMenu = new Clipsy.Views.TrayMenuWindow();
        _trayMenu.CaptureClicked              += OnCaptureRequested;
        _trayMenu.OpenScreenshotsFolderClicked+= OnOpenFolderRequested;
        _trayMenu.OpenVideoFolderClicked      += OnOpenVideoFolderRequested;
        _trayMenu.SettingsClicked             += OnSettingsRequested;
        _trayMenu.UpdateStatusClicked         += () => UpdateManager.PrimaryAction();
        _trayMenu.ExitClicked                 += OnExitRequested;
        UpdateManager.Init(HostWindow.DispatcherQueue);
        ToastService.Prewarm();

        SingleInstanceService.SetRequestHandler(HandleCliRequest);

        Hotkey = new HotkeyService(HostWindow.DispatcherQueue);
        RegisterHotkeys();
        SettingsService.Instance.SettingsChanged += OnSettingsChangedRewireHotkeys;

        // Pre-create + warm the Settings window off-screen so the first open is
        // instant and never shows WinUI's black first-paint frame.
        Clipsy.Views.Settings.SettingsWindow.Prewarm();

        _ = CheckUpdatesIfDueAsync();
        StartUpdateTimer();
        RecordingController.RecoverOrphanedRecordings();

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
    private void StartWatchdogHeartbeat()
    {
        var dq = HostWindow?.DispatcherQueue;
        if (dq == null) return;
        ProcessWatchdog.Pulse();
        SingleInstanceService.MarkUiAlive();
        _watchdogTimer = dq.CreateTimer();
        _watchdogTimer.Interval = TimeSpan.FromSeconds(5);
        _watchdogTimer.IsRepeating = true;
        _watchdogTimer.Tick += (_, _) =>
        {
            ProcessWatchdog.Pulse();
            SingleInstanceService.MarkUiAlive();
        };
        _watchdogTimer.Start();
    }

    private void StartUpdateTimer()
    {
        var dq = HostWindow?.DispatcherQueue;
        if (dq == null) return;
        _updateTimer = dq.CreateTimer();
        _updateTimer.Interval = TimeSpan.FromMinutes(30);
        _updateTimer.IsRepeating = true;
        _updateTimer.Tick += (_, _) => _ = CheckUpdatesIfDueAsync();
        _updateTimer.Start();

        // Separate fast tick: auto-download a pending update once the PC is idle.
        _idleTimer = dq.CreateTimer();
        _idleTimer.Interval = TimeSpan.FromSeconds(15);
        _idleTimer.IsRepeating = true;
        _idleTimer.Tick += (_, _) => UpdateManager.MaybeAutoDownload();
        _idleTimer.Start();
    }

    public Task CheckUpdatesIfDueAsync(bool force = false) => UpdateManager.CheckAsync(force);

    private string HandleCliRequest(string requestJson)
    {
        if (!CliService.TryParseRequest(requestJson, out var request) || request == null)
            return CliService.SerializeResult(new CliResult(2, "Invalid command request."));
        var dq = HostWindow?.DispatcherQueue;
        if (dq == null) return CliService.SerializeResult(new CliResult(3, "UI is not ready."));

        var tcs = new TaskCompletionSource<CliResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dq.TryEnqueue(() =>
        {
            try { tcs.TrySetResult(ExecuteCliCommand(request)); }
            catch (Exception ex) { tcs.TrySetResult(new CliResult(4, ex.Message)); }
        }))
            return CliService.SerializeResult(new CliResult(3, "Could not queue command on UI thread."));

        if (!tcs.Task.Wait(TimeSpan.FromSeconds(5)))
            return CliService.SerializeResult(new CliResult(3, "Command timed out."));
        return CliService.SerializeResult(tcs.Task.Result);
    }

    private CliResult ExecuteCliCommand(CliIpcRequest request)
    {
        switch (request.Command.ToLowerInvariant())
        {
            case "status":
                return new CliResult(0, "running", new
                {
                    running = true,
                    version = UpdateService.CurrentVersion(),
                    executable = Environment.ProcessPath ?? string.Empty,
                    pid = Environment.ProcessId,
                });
            case "capture":
                OnCaptureRequested();
                return new CliResult(0, "capture opened", new { opened = true });
            case "open-settings":
                OnSettingsRequested();
                return new CliResult(0, "settings opened", new { opened = true });
            case "config":
                return CliConfigService.Execute(request.Args);
            case "quit":
                _uiQueue?.TryEnqueue(ExitApp);
                return new CliResult(0, "exiting");
            case "activate":
                NotificationService.Post(NotificationLevel.Info, Strings.Get("AlreadyRunningTitle"),
                    Strings.Get("AlreadyRunningBody"), ToastCategory.Hint);
                return new CliResult(0, "already running");
            default:
                return new CliResult(2, $"Unsupported live command: {request.Command}");
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
        CaptureOverlayHost.RequestOverlay();
    }

    // Runs on the hotkey thread: must not touch WinUI objects directly.
    private void OnCaptureHotkeyRequested()
    {
        if (RecordingController.IsRecording)
        {
            _uiQueue?.TryEnqueue(() => RecordingController.Current?.StopFromHotkey());
            return;
        }
        CaptureOverlayHost.RequestOverlay();
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
        bool ok = Hotkey!.Register(OnCaptureHotkeyRequested, capture, OnRecordStopRequested, record, OnMicToggleRequested, mic);
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

    private bool _exiting;

    /// <summary>Full shutdown path (recording finalize, watchdog, tray) for callers outside App.</summary>
    public void ExitApp() => OnExitRequested();

    private async void OnExitRequested()
    {
        if (_exiting) return;
        _exiting = true;
        // Finish the recording first: the watchdog kills the process ~10 s after a clean-exit mark.
        try
        {
            if (RecordingController.Current is { } recording)
                await recording.StopForExitAsync(TimeSpan.FromSeconds(90));
        }
        catch (Exception ex) { Diagnostics.Log("Exit: finishing recording failed", ex); }

        SingleInstanceService.StopServer();
        ProcessWatchdog.MarkCleanExit();
        _watchdogTimer?.Stop();
        CaptureOverlayHost.Shutdown();
        SettingsService.Instance.SettingsChanged -= OnSettingsChangedRewireHotkeys;
        Hotkey?.Dispose();
        HostWindow?.TrayIconControl.Dispose();
        _trayMenu?.PrepareForShutdown();
        _trayMenu?.Close();
        _trayMenu = null;
        Application.Current.Exit();
    }

    private void OnOpenVideoFolderRequested()
        => OpenFolder(() => SettingsService.Instance.GetEffectiveVideoFolder());

    private void OnOpenFolderRequested()
        => OpenFolder(() => SettingsService.Instance.GetEffectiveScreenshotFolder());

    private static void OpenFolder(Func<string> resolve)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = resolve(),
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Open folder failed", ex);
            NotificationService.Error("ErrOpenFolder");
        }
    }
}
