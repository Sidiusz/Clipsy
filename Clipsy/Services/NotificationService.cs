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
        bool    action2IsPrimary    = false)
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
            ToastCategory.Screenshot,
            action1Icon:    "\xE8E5",
            action1Tooltip: Strings.Get("ToastOpenFile"),
            action1:        () => OpenFile(filePath),
            action2Icon:    "\xE838",
            action2Tooltip: Strings.Get("ToastOpenFolder"),
            action2:        () => OpenFolder(filePath));
    }

    // ── Clipboard ────────────────────────────────────────────────

    public static void CopiedToClipboard()
        => Post(NotificationLevel.Info, Strings.Get("ToastCopied"), null, ToastCategory.Screenshot);

    // ── Update available ─────────────────────────────────────────

    public static void UpdateAvailable(string version, string notes, string releaseUrl, Action skipVersion)
    {
        Post(
            NotificationLevel.Info,
            $"Clipsy {version}",
            notes,
            ToastCategory.Update,
            action1Icon:     "\xE769",
            action1Tooltip:  Strings.Get("ToastSkipVersion"),
            action1:         skipVersion,
            action2Icon:     "\xE896",
            action2Tooltip:  Strings.Get("ToastDownload"),
            action2:         () => OpenUrl(releaseUrl),
            action2IsPrimary: true);
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
