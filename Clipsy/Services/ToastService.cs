using System;
using System.Collections.Generic;
using Clipsy.Views;

namespace Clipsy.Services;

public enum ToastCategory { Screenshot, Video, Clipboard, Error, Update, Hint, Prompt }

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
        // Labelled buttons under the text instead of icons, for choices that need words.
        public string? Action1Text     { get; init; }
        public string? Action2Text     { get; init; }
        // When true the toast never auto-dismisses — it stays until the user
        // clicks an action or Close. Used for update prompts.
        public bool    Persistent      { get; init; }
        // Auto-dismiss delay in seconds for non-persistent toasts (default 5).
        public int     DismissSeconds  { get; init; } = 5;
    }

    private const int MaxVisibleToasts = 4;
    private const int MaxIncomingToasts = 16;

    private sealed record ToastKey(
        ToastCategory Category,
        NotificationLevel Level,
        string Title,
        string Body);

    private sealed record ActiveToast(ToastWindow Window, ToastKey Key);

    private static readonly object _incomingGate = new();
    private static readonly Queue<ToastOptions> _incoming = new();
    private static readonly HashSet<ToastKey> _incomingKeys = new();
    private static bool _incomingScheduled;

    // Mutated on UI thread only.
    private static readonly List<ToastWindow> _pool = new();
    private static readonly List<ActiveToast> _active = new();
    private static readonly HashSet<ToastKey> _known = new();

    internal static void Prewarm()
    {
        var dq = App.Current?.HostWindow?.DispatcherQueue;
        if (dq == null) return;
        dq.TryEnqueue(EnsurePoolOnUiThread);
    }

    private static void EnsurePoolOnUiThread()
    {
        while (_pool.Count < MaxVisibleToasts)
        {
            var toast = new ToastWindow();
            toast.Dismissed += OnToastDismissed;
            toast.Prewarm();
            _pool.Add(toast);
        }
    }

    public static void Show(ToastOptions opts)
    {
        var s = SettingsService.Instance.Settings;
        // Prompts are questions the app asked, not notifications.
        if (!s.NotificationsEnabled && opts.Category != ToastCategory.Prompt) return;
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
        EnsurePoolOnUiThread();
        var key = KeyOf(opts);
        if (_known.Contains(key))
            return;

        if (_active.Count >= MaxVisibleToasts)
            return;

        _known.Add(key);
        ShowNow(opts, key);
    }

    private static void ShowNow(ToastOptions opts, ToastKey key)
    {
        ToastWindow? toast = null;
        foreach (var candidate in _pool)
        {
            if (!candidate.IsInUse)
            {
                toast = candidate;
                break;
            }
        }

        if (toast == null)
        {
            _known.Remove(key);
            return;
        }

        try
        {
            toast.ShowToast(opts, _active.Count);
            _active.Add(new ActiveToast(toast, key));
        }
        catch (Exception ex)
        {
            _known.Remove(key);
            Diagnostics.Log("ToastService.ShowNow", ex);
        }
    }

    private static void OnToastDismissed(object? sender, EventArgs e)
    {
        if (sender is not ToastWindow tw)
            return;

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_active[i].Window, tw))
                continue;

            _known.Remove(_active[i].Key);
            _active.RemoveAt(i);
            break;
        }

        RepositionAll();
    }

    internal static void RepositionAll()
    {
        for (int i = 0; i < _active.Count; i++)
            _active[i].Window.PositionAtSlot(i);
    }
}
