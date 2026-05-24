using System;
using Clipsy.Localization;

namespace Clipsy.Services;

public enum NotificationLevel { Info, Warning, Error }

public static class NotificationService
{
    public sealed record Notification(NotificationLevel Level, string Title, string Body);

    public static event Action<Notification>? Posted;

    public static void Post(
        NotificationLevel level, string title, string body,
        ToastCategory category = ToastCategory.Hint,
        string? action1Label = null, Action? action1 = null,
        string? action2Label = null, Action? action2 = null)
    {
        Posted?.Invoke(new Notification(level, title, body));

        ToastService.Show(new ToastService.ToastOptions
        {
            Category        = category,
            Level           = level,
            Title           = title,
            Body            = body,
            Action1Label    = action1Label,
            Action1Callback = action1,
            Action2Label    = action2Label,
            Action2Callback = action2,
        });
    }

    public static void Error(string bodyKey)
        => Post(NotificationLevel.Error,   "Clipsy", Strings.Get(bodyKey), ToastCategory.Error);

    public static void Warning(string bodyKey)
        => Post(NotificationLevel.Warning, "Clipsy", Strings.Get(bodyKey), ToastCategory.Hint);

    public static void Info(string bodyKey)
        => Post(NotificationLevel.Info,    "Clipsy", Strings.Get(bodyKey), ToastCategory.Hint);

    public static void InfoText(string title, string body)
        => Post(NotificationLevel.Info, title, body, ToastCategory.Hint);

    public static void ScreenshotSaved(string fileName, long sizeKb, string filePath)
    {
        var sizeText = sizeKb >= 1024
            ? $"{sizeKb / 1024.0:F1} MB"
            : $"{sizeKb} KB";
        Post(
            NotificationLevel.Info,
            Strings.Get("ToastScreenshotSaved"),
            $"{fileName} · {sizeText}",
            ToastCategory.Screenshot,
            action1Label: Strings.Get("ToastOpen"),
            action1: () =>
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(filePath) { UseShellExecute = true });
                }
                catch { }
            });
    }

    public static void UpdateAvailable(string version, string notes)
        => Post(NotificationLevel.Info, $"Clipsy {version}", notes, ToastCategory.Update);
}
