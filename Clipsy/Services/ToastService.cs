using System;
using System.Collections.Generic;
using Clipsy.Views;

namespace Clipsy.Services;

public enum ToastCategory { Screenshot, Video, Clipboard, Error, Update, Hint }

public static class ToastService
{
    public sealed class ToastOptions
    {
        public ToastCategory Category { get; init; } = ToastCategory.Hint;
        public NotificationLevel Level { get; init; } = NotificationLevel.Info;
        public required string Title { get; init; }
        public string? Body { get; init; }
        // Icon buttons with tooltips (Action1 = secondary/ghost, Action2 = primary when Action2IsPrimary)
        public string? Action1Icon     { get; init; }
        public string? Action1Tooltip  { get; init; }
        public Action? Action1Callback { get; init; }
        public string? Action2Icon     { get; init; }
        public string? Action2Tooltip  { get; init; }
        public Action? Action2Callback { get; init; }
        public bool    Action2IsPrimary { get; init; }
        // When true the toast never auto-dismisses — it stays until the user
        // clicks an action or Close. Used for update prompts.
        public bool    Persistent      { get; init; }
        // Auto-dismiss delay in seconds for non-persistent toasts (default 4).
        public int     DismissSeconds  { get; init; } = 4;
    }

    // Mutated on UI thread only.
    private static readonly List<ToastWindow> _active = new();

    public static void Show(ToastOptions opts)
    {
        var s = SettingsService.Instance.Settings;
        if (!s.NotificationsEnabled) return;
        if (opts.Category == ToastCategory.Screenshot && !s.NotifyScreenshotSaved) return;
        if (opts.Category == ToastCategory.Video      && !s.NotifyVideoSaved)       return;
        if (opts.Category == ToastCategory.Clipboard  && !s.NotifyClipboard)        return;
        if (opts.Category == ToastCategory.Error      && !s.NotifyErrors)           return;
        if (opts.Category == ToastCategory.Update     && !s.NotifyUpdateAvailable)  return;
        if (opts.Category == ToastCategory.Hint       && !s.NotifyHints)            return;

        var dq = App.Current?.HostWindow?.DispatcherQueue;
        if (dq == null) return;
        dq.TryEnqueue(() => ShowOnUiThread(opts));
    }

    private static void ShowOnUiThread(ToastOptions opts)
    {
        try
        {
            var toast = new ToastWindow(opts);
            toast.Closed += OnToastClosed;
            _active.Add(toast);
            RepositionAll();
        }
        catch (Exception ex)
        {
            Diagnostics.Log("ToastService.ShowOnUiThread", ex);
        }
    }

    private static void OnToastClosed(object? sender, Microsoft.UI.Xaml.WindowEventArgs e)
    {
        if (sender is ToastWindow tw)
        {
            tw.Closed -= OnToastClosed;
            _active.Remove(tw);
            RepositionAll();
        }
    }

    internal static void RepositionAll()
    {
        for (int i = 0; i < _active.Count; i++)
            _active[i].PositionAtSlot(i);
    }
}
