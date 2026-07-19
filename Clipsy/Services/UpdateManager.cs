using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
    public static string? InstallerPath { get; private set; }

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
            s.LastUpdateCheckUtc = DateTime.UtcNow;
            SettingsService.Instance.Save();

            if (result.Status == UpdateCheckStatus.Failed)
            {
                if (force) NotificationService.Warning("UpdateCheckFailed");
                SetPhase(UpdatePhase.Failed);
                return;
            }
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
            Info = info;
            SetPhase(UpdatePhase.Available);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("UpdateManager.CheckAsync", ex);
            SetPhase(UpdatePhase.Failed);
        }
    }

    // Tray/Settings primary action: download when available, install when ready,
    // re-check on failure.
    public static void PrimaryAction()
    {
        switch (Phase)
        {
            case UpdatePhase.Available: _ = StartDownloadAsync(); break;
            case UpdatePhase.Ready:     InstallNow();             break;
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
        string? path = await UpdateService.DownloadInstallerAsync(Info, prog);
        _downloading = false;
        if (path == null) { SetPhase(UpdatePhase.Failed); return; }
        InstallerPath = path;
        SetPhase(UpdatePhase.Ready);
    }

    public static void InstallNow()
    {
        if (string.IsNullOrEmpty(InstallerPath)) return;
        if (UpdateService.LaunchInstaller(InstallerPath))
            try { Microsoft.UI.Xaml.Application.Current.Exit(); } catch { }
    }

    public static void SkipCurrent()
    {
        if (Info == null) return;
        SettingsService.Instance.Settings.SkippedVersion = Info.Version;
        SettingsService.Instance.Save();
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
