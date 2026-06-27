using System;
using System.IO;
using Clipsy.Localization;

namespace Clipsy.Services;

public enum NotificationLevel { Info, Warning, Error }

public static class NotificationService
{
    public sealed record Notification(NotificationLevel Level, string Title, string Body);

    public static event Action<Notification>? Posted;

    public static void Post(
        NotificationLevel level,
        string title,
        string? body,
        ToastCategory category      = ToastCategory.Hint,
        string? action1Icon         = null,
        string? action1Tooltip      = null,
        Action? action1             = null,
        string? action2Icon         = null,
        string? action2Tooltip      = null,
        Action? action2             = null,
        bool    action2IsPrimary    = false,
        bool    persistent          = false)
    {
        Posted?.Invoke(new Notification(level, title, body ?? string.Empty));

        ToastService.Show(new ToastService.ToastOptions
        {
            Category        = category,
            Level           = level,
            Title           = title,
            Body            = body,
            Action1Icon     = action1Icon,
            Action1Tooltip  = action1Tooltip,
            Action1Callback = action1,
            Action2Icon     = action2Icon,
            Action2Tooltip  = action2Tooltip,
            Action2Callback = action2,
            Action2IsPrimary = action2IsPrimary,
            Persistent      = persistent,
        });
    }

    // ── Simple helpers ───────────────────────────────────────────

    public static void Error(string bodyKey)
        => Post(NotificationLevel.Error,   "Clipsy", Strings.Get(bodyKey), ToastCategory.Error);

    public static void Warning(string bodyKey)
        => Post(NotificationLevel.Warning, "Clipsy", Strings.Get(bodyKey), ToastCategory.Hint);

    public static void Info(string bodyKey)
        => Post(NotificationLevel.Info,    "Clipsy", Strings.Get(bodyKey), ToastCategory.Hint);

    public static void InfoText(string title, string body)
        => Post(NotificationLevel.Info, title, body, ToastCategory.Hint);

    // ── Screenshot saved ─────────────────────────────────────────

    public static void ScreenshotSaved(string fileName, long sizeKb, string filePath)
    {
        Post(
            NotificationLevel.Info,
            Strings.Get("ToastScreenshotSaved"),
            $"{fileName} · {FormatSize(sizeKb)}",
            ToastCategory.Screenshot,
            action1Icon:    "\xE8E5",
            action1Tooltip: Strings.Get("ToastOpenFile"),
            action1:        () => OpenFile(filePath),
            action2Icon:    "\xE838",
            action2Tooltip: Strings.Get("ToastOpenFolder"),
            action2:        () => OpenFolder(filePath));
    }

    // ── Video saved ──────────────────────────────────────────────

    public static void VideoSaved(string fileName, long sizeKb, string filePath)
    {
        Post(
            NotificationLevel.Info,
            Strings.Get("ToastVideoSaved"),
            $"{fileName} · {FormatSize(sizeKb)}",
            ToastCategory.Video,
            action1Icon:    "\xE8E5",
            action1Tooltip: Strings.Get("ToastOpenFile"),
            action1:        () => OpenFile(filePath),
            action2Icon:    "\xE838",
            action2Tooltip: Strings.Get("ToastOpenFolder"),
            action2:        () => OpenFolder(filePath));
    }

    // ── Video saved as MP4 fallback (AVI/MKV needs FFmpeg) ───────

    public static void VideoSavedAsMp4(string fileName, long sizeKb, string filePath, string requestedFmt)
    {
        var body = string.Format(
            Strings.Get("WarnSavedAsMp4"),
            requestedFmt.ToUpperInvariant(),
            $"{fileName} · {FormatSize(sizeKb)}");

        Post(
            NotificationLevel.Warning,
            Strings.Get("ToastVideoSaved"),
            body,
            ToastCategory.Video,
            action1Icon:    "\xE713",  // Settings gear
            action1Tooltip: Strings.Get("ToastGetFfmpeg"),
            action1:        OpenVideoSettings,
            action2Icon:    "\xE838",  // Folder
            action2Tooltip: Strings.Get("ToastOpenFolder"),
            action2:        () => OpenFolder(filePath));
    }

    private static void OpenVideoSettings()
    {
        try { Clipsy.Views.Settings.SettingsWindow.ShowOrActivate(); }
        catch { }
    }

    // ── Clipboard ────────────────────────────────────────────────

    public static void CopiedToClipboard()
        => Post(NotificationLevel.Info, Strings.Get("ToastCopied"), null, ToastCategory.Clipboard);

    // ── Update available ─────────────────────────────────────────

    public static void UpdateAvailable(UpdateInfo info, string notes, Action skipVersion)
    {
        Post(
            NotificationLevel.Info,
            $"Clipsy {info.Version}",
            notes,
            ToastCategory.Update,
            action1Icon:     "\xE769",
            action1Tooltip:  Strings.Get("ToastSkipVersion"),
            action1:         skipVersion,
            action2Icon:     "\xE896",
            action2Tooltip:  Strings.Get("ToastDownload"),
            action2:         () => StartUpdate(info),
            action2IsPrimary: true,
            persistent:      true);
    }

    // Download the installer asset and hand off (app must exit to be overwritten);
    // missing asset or failed download falls back to the release page.
    private static async void StartUpdate(UpdateInfo info)
    {
        if (!string.IsNullOrEmpty(info.InstallerUrl))
        {
            Post(NotificationLevel.Info, Strings.Get("ToastUpdateDownloading"), null, ToastCategory.Update);
            bool ok = await UpdateService.DownloadAndLaunchInstallerAsync(info);
            if (ok)
            {
                try { Microsoft.UI.Xaml.Application.Current.Exit(); } catch { }
                return;
            }
            Post(NotificationLevel.Warning, Strings.Get("ToastUpdateDownloadFailed"), null, ToastCategory.Update);
        }
        OpenUrl(info.Url);
    }

    // ── Private helpers ──────────────────────────────────────────

    private static string FormatSize(long sizeKb)
        => sizeKb >= 1024 ? $"{sizeKb / 1024.0:F1} MB" : $"{sizeKb} KB";

    private static void OpenFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch { }
    }

    private static void OpenFolder(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                    { UseShellExecute = true });
        }
        catch { }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
