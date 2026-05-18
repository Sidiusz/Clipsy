using System;
using Clipsy.Localization;
using H.NotifyIcon;
using H.NotifyIcon.Core;

namespace Clipsy.Services;

public enum NotificationLevel { Info, Warning, Error }

/// <summary>
/// Shows lightweight notifications. Falls back to the system tray
/// balloon (H.NotifyIcon NotificationIcon) when no foreground window
/// is available. Code-behind that owns an InfoBar can subscribe to
/// Posted and render in-window.
/// </summary>
public static class NotificationService
{
    public sealed record Notification(NotificationLevel Level, string Title, string Body);

    public static event Action<Notification>? Posted;

    public static void Post(NotificationLevel level, string title, string body)
    {
        var n = new Notification(level, title, body);
        Posted?.Invoke(n);
        TryTrayBalloon(n);
    }

    public static void Error(string bodyKey)        => Post(NotificationLevel.Error,   "Clipsy", Strings.Get(bodyKey));
    public static void Warning(string bodyKey)      => Post(NotificationLevel.Warning, "Clipsy", Strings.Get(bodyKey));
    public static void Info(string bodyKey)         => Post(NotificationLevel.Info,    "Clipsy", Strings.Get(bodyKey));
    public static void InfoText(string title, string body) => Post(NotificationLevel.Info, title, body);

    private static void TryTrayBalloon(Notification n)
    {
        try
        {
            var host = App.Current.HostWindow;
            if (host == null) return;
            var tray = host.TrayIconControl;
            if (tray == null) return;
            var icon = n.Level switch
            {
                NotificationLevel.Error => NotificationIcon.Error,
                NotificationLevel.Warning => NotificationIcon.Warning,
                _ => NotificationIcon.Info,
            };
            tray.ShowNotification(n.Title, n.Body, icon);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Tray balloon failed: {ex.Message}");
        }
    }
}
