using System;
using System.IO;
using Clipsy.Localization;

namespace Clipsy.Services;

public enum NotificationLevel { Info, Warning, Error }

public static class NotificationService
{
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
        bool    persistent          = false,
        int     dismissSeconds      = 0)
    {
        int duration = dismissSeconds > 0
            ? Math.Clamp(dismissSeconds, 1, 30)
            : Math.Clamp(SettingsService.Instance.Settings.NotificationDurationSeconds, 1, 30);

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
            DismissSeconds  = duration,
        });
    }

    // ── Simple helpers ───────────────────────────────────────────

    // Prompts answer a question the app asked, so they ignore the per-category notification switches.
    public static void Prompt(string body, NotificationLevel level = NotificationLevel.Info)
        => ToastService.Show(new ToastService.ToastOptions
        {
            Category = ToastCategory.Prompt,
            Level = level,
            Title = "Clipsy",
            Body = body,
            DismissSeconds = Math.Clamp(SettingsService.Instance.Settings.NotificationDurationSeconds, 1, 30),
        });

    public static void OcrEngineOffer(string title, string body, string keepText, Action keep, string switchText, Action switchAction)
        => ToastService.Show(new ToastService.ToastOptions
        {
            Category = ToastCategory.Prompt,
            Title = title,
            Body = body,
            Action1Text = keepText,
            Action1Callback = keep,
            Action2Text = switchText,
            Action2Callback = switchAction,
            Action2IsPrimary = true,
            Persistent = true,
        });

    public static void Error(string bodyKey)
        => Post(NotificationLevel.Error,   "Clipsy", Strings.Get(bodyKey), ToastCategory.Error);

    public static void Warning(string bodyKey)
        => Post(NotificationLevel.Warning, "Clipsy", Strings.Get(bodyKey), ToastCategory.Hint);

    public static void Info(string bodyKey)
        => Post(NotificationLevel.Info,    "Clipsy", Strings.Get(bodyKey), ToastCategory.Hint);

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

    public static void VideoSavedAsMp4(string fileName, long sizeKb, string filePath, string requestedFmt, bool ffmpegMissing)
    {
        var body = string.Format(
            Strings.Get(ffmpegMissing ? "WarnSavedAsMp4" : "WarnConvertFailedMp4"),
            requestedFmt.ToUpperInvariant(),
            $"{fileName} · {FormatSize(sizeKb)}");

        Post(
            NotificationLevel.Warning,
            Strings.Get("ToastVideoSaved"),
            body,
            ToastCategory.Video,
            action1Icon:    ffmpegMissing ? "\xE713" : null,  // Settings gear
            action1Tooltip: ffmpegMissing ? Strings.Get("ToastGetFfmpeg") : null,
            action1:        ffmpegMissing ? OpenVideoSettings : null,
            action2Icon:    "\xE838",  // Folder
            action2Tooltip: Strings.Get("ToastOpenFolder"),
            action2:        () => OpenFolder(filePath));
    }

    public static void VideoKeptAfterFailure(string filePath)
        => Post(NotificationLevel.Error, "Clipsy",
            string.Format(Strings.Get("ErrVideoKept"), filePath),
            ToastCategory.Error,
            action2Icon: "\xE838",
            action2Tooltip: Strings.Get("ToastOpenFolder"),
            action2: () => OpenFolder(filePath),
            persistent: true);

    public static void VideoRecovered(string filePath)
        => Post(NotificationLevel.Info, Strings.Get("ToastVideoSaved"),
            Strings.Get("ToastVideoRecovered"),
            ToastCategory.Video,
            action2Icon: "\xE838",
            action2Tooltip: Strings.Get("ToastOpenFolder"),
            action2: () => OpenFolder(filePath));

    private static void OpenVideoSettings()
    {
        try { Clipsy.Views.Settings.SettingsWindow.ShowOrActivate(); }
        catch { }
    }

    // ── Clipboard ────────────────────────────────────────────────

    public static void CopiedToClipboard()
        => Post(NotificationLevel.Info, Strings.Get("ToastCopied"), null, ToastCategory.Clipboard);

    // ── Update available ─────────────────────────────────────────

    // Update flow lives in UpdateManager (tray + Settings); this only opens the
    // release page when no installer asset is available to auto-download.
    public static void OpenReleasePage(string url) => OpenUrl(url);

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
