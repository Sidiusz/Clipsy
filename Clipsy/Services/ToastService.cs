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
        // Auto-dismiss delay in seconds for non-persistent toasts (default 5).
        public int     DismissSeconds  { get; init; } = 5;
    }

    private const int MaxVisibleToasts = 4;
    private const int MaxQueuedToasts = 12;
    private const int MaxIncomingToasts = 32;

    private sealed record ToastKey(
        ToastCategory Category,
        NotificationLevel Level,
        string Title,
        string Body);

    private sealed record ActiveToast(ToastWindow Window, ToastKey Key);
    private sealed record QueuedToast(ToastOptions Options, ToastKey Key);

    private static readonly object _incomingGate = new();
    private static readonly Queue<ToastOptions> _incoming = new();
    private static readonly HashSet<ToastKey> _incomingKeys = new();
    private static bool _incomingScheduled;

    // Mutated on UI thread only.
    private static readonly List<ActiveToast> _active = new();
    private static readonly Queue<QueuedToast> _pending = new();
    private static readonly HashSet<ToastKey> _known = new();

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

        var key = KeyOf(opts);
        bool scheduleDrain = false;

        lock (_incomingGate)
        {
            if (_incomingKeys.Contains(key))
                return;
            if (_incoming.Count >= MaxIncomingToasts)
                return;

            _incoming.Enqueue(opts);
            _incomingKeys.Add(key);

            if (!_incomingScheduled)
            {
                _incomingScheduled = true;
                scheduleDrain = true;
            }
        }

        if (scheduleDrain && !dq.TryEnqueue(DrainIncomingOnUiThread))
        {
            lock (_incomingGate)
            {
                _incomingScheduled = false;
                _incoming.Clear();
                _incomingKeys.Clear();
            }
        }
    }

    private static ToastKey KeyOf(ToastOptions opts) =>
        new(opts.Category, opts.Level, opts.Title, opts.Body ?? string.Empty);

    private static void DrainIncomingOnUiThread()
    {
        List<ToastOptions> batch = new();

        lock (_incomingGate)
        {
            while (_incoming.Count > 0)
                batch.Add(_incoming.Dequeue());

            _incomingKeys.Clear();
            _incomingScheduled = false;
        }

        foreach (var opts in batch)
            ShowOnUiThread(opts);
    }

    private static void ShowOnUiThread(ToastOptions opts)
    {
        var key = KeyOf(opts);
        if (_known.Contains(key))
            return;

        if (_active.Count >= MaxVisibleToasts)
        {
            if (_pending.Count >= MaxQueuedToasts)
                return;

            _known.Add(key);
            _pending.Enqueue(new QueuedToast(opts, key));
            return;
        }

        _known.Add(key);
        ShowNow(opts, key);
    }

    private static void ShowNow(ToastOptions opts, ToastKey key)
    {
        try
        {
            var toast = new ToastWindow(opts);
            toast.Closed += OnToastClosed;
            _active.Add(new ActiveToast(toast, key));
            RepositionAll();
        }
        catch (Exception ex)
        {
            _known.Remove(key);
            Diagnostics.Log("ToastService.ShowNow", ex);
        }
    }

    private static void OnToastClosed(object? sender, Microsoft.UI.Xaml.WindowEventArgs e)
    {
        if (sender is not ToastWindow tw)
            return;

        tw.Closed -= OnToastClosed;

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_active[i].Window, tw))
                continue;

            _known.Remove(_active[i].Key);
            _active.RemoveAt(i);
            break;
        }

        RepositionAll();
        DrainPending();
    }

    private static void DrainPending()
    {
        while (_active.Count < MaxVisibleToasts && _pending.Count > 0)
        {
            var item = _pending.Dequeue();
            ShowNow(item.Options, item.Key);
        }
    }

    internal static void RepositionAll()
    {
        for (int i = 0; i < _active.Count; i++)
            _active[i].Window.PositionAtSlot(i);
    }
}
