using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Clipsy.Localization;
using Microsoft.UI.Dispatching;

namespace Clipsy.Services;

public enum UpdatePhase { None, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <summary>Shared update state driven by the tray and Settings alike. Owns the
/// check/download/install flow plus idle-gated auto-download.</summary>
public static class UpdateManager
{
    // Auto-download only kicks in after the user has been idle this long.
    private const uint AutoDownloadIdleMs = 60_000;

    public static UpdatePhase Phase { get; private set; } = UpdatePhase.None;
    public static UpdateInfo? Info { get; private set; }
    public static double Progress { get; private set; }
    private static UpdateService.VerifiedInstaller? _installer;
    private static bool _downloadFailed;

    public static event Action? StateChanged;

    private static DispatcherQueue? _ui;
    private static bool _downloading;

    public static void Init(DispatcherQueue ui) => _ui = ui;

    private static void SetPhase(UpdatePhase p) { Phase = p; Raise(); }

    private static void Raise()
    {
        if (_ui == null) { try { StateChanged?.Invoke(); } catch { } return; }
        _ui.TryEnqueue(() => { try { StateChanged?.Invoke(); } catch (Exception ex) { Diagnostics.Log("UpdateManager.Raise", ex); } });
    }

    public static async Task CheckAsync(bool force)
    {
        try
        {
            var s = SettingsService.Instance.Settings;
            if (!force)
            {
                if (s.UpdateInterval == "never") return;
                if (!UpdateService.ShouldCheckNow(s.UpdateInterval, s.LastUpdateCheckUtc)) return;
            }
            // A download already in flight or ready must not be reset by a re-check.
            if (Phase is UpdatePhase.Downloading or UpdatePhase.Ready) return;

            SetPhase(UpdatePhase.Checking);
            var result = await UpdateService.CheckLatestAsync();
            if (result.Status == UpdateCheckStatus.Failed)
            {
                if (force) NotificationService.Warning("UpdateCheckFailed");
                SetPhase(UpdatePhase.Failed);
                return;
            }
            // Only a successful check counts, so an offline boot retries on the next tick.
            s = SettingsService.Instance.Settings;
            s.LastUpdateCheckUtc = DateTime.UtcNow;
            SettingsService.Instance.SaveState();
            var info = result.Info;
            if (info == null || !UpdateService.IsNewer(info.Version, UpdateService.CurrentVersion()))
            {
                if (force) NotificationService.Info("UpdateUpToDate");
                Info = null;
                SetPhase(UpdatePhase.UpToDate);
                return;
            }
            if (!force && info.Version == s.SkippedVersion)
            {
                SetPhase(UpdatePhase.UpToDate);
                return;
            }
            bool isNew = Info?.Version != info.Version;
            Info = info;
            _downloadFailed = false;
            SetPhase(UpdatePhase.Available);
            if (isNew && !force) NotifyAvailable(info);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("UpdateManager.CheckAsync", ex);
            SetPhase(UpdatePhase.Failed);
        }
    }

    private static void NotifyAvailable(UpdateInfo info)
        => NotificationService.Post(NotificationLevel.Info, "Clipsy",
            string.Format(Strings.Get("NotifyUpdateAvailable"), info.Version),
            ToastCategory.Update,
            action1Icon: "\uE894", action1Tooltip: Strings.Get("ToastSkipVersion"), action1: SkipCurrent,
            action2Icon: "\uE896", action2Tooltip: Strings.Get("ToastDownload"), action2: () => _ = StartDownloadAsync(),
            action2IsPrimary: true);

    // Tray/Settings primary action: download when available, install when ready; after a failed
    // download send the user to the release page, after a failed check re-check.
    public static void PrimaryAction()
    {
        switch (Phase)
        {
            case UpdatePhase.Available: _ = StartDownloadAsync(); break;
            case UpdatePhase.Ready:     InstallNow();             break;
            case UpdatePhase.Failed when _downloadFailed && Info != null:
                NotificationService.OpenReleasePage(Info.Url);
                break;
            case UpdatePhase.Failed:    _ = CheckAsync(true);     break;
        }
    }

    public static async Task StartDownloadAsync()
    {
        if (_downloading || Info == null) return;
        if (string.IsNullOrEmpty(Info.InstallerUrl))
        {
            // No installer asset — send the user to the release page instead.
            NotificationService.OpenReleasePage(Info.Url);
            return;
        }
        _downloading = true;
        Progress = 0;
        SetPhase(UpdatePhase.Downloading);
        var prog = new Progress<double>(p => { Progress = p; Raise(); });
        var installer = await UpdateService.DownloadInstallerAsync(Info, prog);
        _downloading = false;
        if (installer == null)
        {
            _downloadFailed = true;
            NotificationService.Warning("ToastUpdateDownloadFailed");
            SetPhase(UpdatePhase.Failed);
            return;
        }
        _installer?.Dispose();
        _installer = installer;
        SetPhase(UpdatePhase.Ready);
    }

    public static void InstallNow()
    {
        if (_installer == null) return;
        // The installer closes Clipsy; don't cut a recording short.
        if (RecordingController.IsBusy)
        {
            NotificationService.Warning("UpdateWaitRecording");
            return;
        }
        if (_installer.Launch())
            App.Current.ExitApp();
        else
        {
            _installer.Dispose();
            _installer = null;
            _downloadFailed = true;
            SetPhase(UpdatePhase.Failed);
        }
    }

    public static void SkipCurrent()
    {
        if (Info == null) return;
        SettingsService.Instance.Settings.SkippedVersion = Info.Version;
        SettingsService.Instance.SaveState();
        Info = null;
        SetPhase(UpdatePhase.UpToDate);
    }

    // Called on a timer: auto-download the pending update once the PC has been
    // idle long enough and the setting allows it.
    public static void MaybeAutoDownload()
    {
        if (!SettingsService.Instance.Settings.AutoDownloadUpdates) return;
        if (Phase != UpdatePhase.Available || _downloading) return;
        if (Info == null || string.IsNullOrEmpty(Info.InstallerUrl)) return;
        if (IdleMilliseconds() < AutoDownloadIdleMs) return;
        _ = StartDownloadAsync();
    }

    private static uint IdleMilliseconds()
    {
        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref lii)) return 0;
        return unchecked((uint)Environment.TickCount - lii.dwTime);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
