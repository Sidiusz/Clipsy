using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Clipsy.Services;

internal static class CliConfigService
{
    private sealed record Entry(string Property, string[]? Allowed = null, int? Min = null, int? Max = null);

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["language"] = new(nameof(AppSettings.Language), ["auto", "en", "ru"]),
        ["theme"] = new(nameof(AppSettings.Theme), ["auto", "dark", "light"]),
        ["ocr.engine"] = new(nameof(AppSettings.OcrEngine), ["WinRT", "Tesseract"]),
        ["ocr.languages"] = new(nameof(AppSettings.TesseractLanguages)),
        ["paths.screenshot"] = new(nameof(AppSettings.ScreenshotFolder)),
        ["paths.video"] = new(nameof(AppSettings.VideoFolder)),
        ["save.remember-last-folder"] = new(nameof(AppSettings.RememberLastFolder)),
        ["save.after"] = new(nameof(AppSettings.AfterSaveAction), ["nothing", "open-file", "open-folder"]),
        ["updates.interval"] = new(nameof(AppSettings.UpdateInterval), ["hourly", "daily", "weekly", "monthly", "never"]),
        ["updates.auto-download"] = new(nameof(AppSettings.AutoDownloadUpdates)),
        ["screenshot.format"] = new(nameof(AppSettings.ScreenshotFormat), ["png", "jpg", "webp"]),
        ["screenshot.jpeg-quality"] = new(nameof(AppSettings.JpgQuality), null, 50, 100),
        ["screenshot.cursor"] = new(nameof(AppSettings.CaptureScreenshotCursor)),
        ["capture.modern"] = new(nameof(AppSettings.ExperimentalModernScreenshotCapture)),
        ["capture.dynamic-toolbar"] = new(nameof(AppSettings.DynamicToolbarIslands)),
        ["video.format"] = new(nameof(AppSettings.VideoFormat), ["mp4", "avi", "mkv", "gif"]),
        ["video.codec"] = new(nameof(AppSettings.VideoCodec), ["H.264", "H.265", "VP9", "AV1"]),
        ["video.resolution"] = new(nameof(AppSettings.VideoResolution), ["480p", "720p", "1080p", "1440p", "Original"]),
        ["video.fps"] = new(nameof(AppSettings.VideoFramerate), null, 0, 240),
        ["video.bitrate-mbps"] = new(nameof(AppSettings.VideoBitrateMbps), null, 1, 50),
        ["video.cursor"] = new(nameof(AppSettings.CaptureVideoCursor)),
        ["audio.microphone"] = new(nameof(AppSettings.MicrophoneEnabled)),
        ["audio.microphone-muted"] = new(nameof(AppSettings.MicrophoneMuted)),
        ["audio.microphone-device"] = new(nameof(AppSettings.MicrophoneDevice)),
        ["eyedropper.modifier"] = new(nameof(AppSettings.EyedropperModifier)),
        ["eyedropper.copy-hex"] = new(nameof(AppSettings.CopyEyedropperHexToClipboard)),
        ["gif.colors"] = new(nameof(AppSettings.GifColors), null, 16, 256),
        ["gif.fps"] = new(nameof(AppSettings.GifFps), null, 5, 30),
        ["gif.dither"] = new(nameof(AppSettings.GifDither)),
        ["hotkey.capture"] = new(nameof(AppSettings.HotkeyCapture)),
        ["hotkey.screenshot"] = new(nameof(AppSettings.HotkeyScreenshotSilent)),
        ["hotkey.copy"] = new(nameof(AppSettings.HotkeyCopy)),
        ["hotkey.undo"] = new(nameof(AppSettings.HotkeyUndo)),
        ["hotkey.redo"] = new(nameof(AppSettings.HotkeyRedo)),
        ["hotkey.select-all"] = new(nameof(AppSettings.HotkeySelectAll)),
        ["hotkey.select-monitor"] = new(nameof(AppSettings.HotkeySelectMonitor)),
        ["hotkey.record-save"] = new(nameof(AppSettings.HotkeyRecordSilentSave)),
        ["hotkey.mic"] = new(nameof(AppSettings.HotkeyMicToggle)),
        ["translation.service"] = new(nameof(AppSettings.TranslateService), ["Google", "MyMemory"]),
        ["translation.from"] = new(nameof(AppSettings.TranslateFrom)),
        ["translation.to"] = new(nameof(AppSettings.TranslateTo)),
        ["notifications.enabled"] = new(nameof(AppSettings.NotificationsEnabled)),
        ["notifications.duration"] = new(nameof(AppSettings.NotificationDurationSeconds), null, 1, 30),
        ["notifications.screenshot"] = new(nameof(AppSettings.NotifyScreenshotSaved)),
        ["notifications.video"] = new(nameof(AppSettings.NotifyVideoSaved)),
        ["notifications.clipboard"] = new(nameof(AppSettings.NotifyClipboard)),
        ["notifications.errors"] = new(nameof(AppSettings.NotifyErrors)),
        ["notifications.update"] = new(nameof(AppSettings.NotifyUpdateAvailable)),
        ["notifications.hints"] = new(nameof(AppSettings.NotifyHints)),
    };

    internal static CliResult Execute(string[] args)
    {
        if (args.Length == 0) return new CliResult(2, "Usage: config list|get|set ...");
        return args[0].ToLowerInvariant() switch
        {
            "list" => List(),
            "get" => Get(args),
            "set" => Set(args),
            _ => new CliResult(2, $"Unknown config command: {args[0]}"),
        };
    }
    private static CliResult List()
    {
        var settings = SettingsService.Instance.Settings;
        var data = new SortedDictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in Entries)
            data[pair.Key] = GetProperty(pair.Value).GetValue(settings);
        var lines = string.Join(Environment.NewLine, data.Select(p => $"{p.Key}={FormatValue(p.Value)}"));
        return new CliResult(0, lines, data);
    }

    private static CliResult Get(string[] args)
    {
        if (args.Length != 2) return new CliResult(2, "Usage: config get KEY");
        if (!TryResolve(args[1], out var key, out var entry))
            return new CliResult(2, $"Unknown setting: {args[1]}");
        var value = GetProperty(entry).GetValue(SettingsService.Instance.Settings);
        return new CliResult(0, $"{key}={FormatValue(value)}", new { key, value });
    }

    private static CliResult Set(string[] args)
    {
        if (args.Length < 3) return new CliResult(2, "Usage: config set KEY VALUE");
        if (!TryResolve(args[1], out var key, out var entry))
            return new CliResult(2, $"Unknown setting: {args[1]}");
        string raw = string.Join(' ', args.Skip(2));
        if (!TryConvert(entry, raw, out var value, out var error))
            return new CliResult(2, error!);

        var updated = SettingsService.Instance.Settings.Clone();
        GetProperty(entry).SetValue(updated, value);
        SettingsService.Instance.Replace(updated);
        var stored = GetProperty(entry).GetValue(SettingsService.Instance.Settings);
        return new CliResult(0, $"{key}={FormatValue(stored)}", new { key, value = stored });
    }
    private static bool TryResolve(string input, out string key, out Entry entry)
    {
        if (Entries.TryGetValue(input, out entry!))
        {
            key = Entries.Keys.First(k => string.Equals(k, input, StringComparison.OrdinalIgnoreCase));
            return true;
        }
        foreach (var pair in Entries)
        {
            if (string.Equals(pair.Value.Property, input, StringComparison.OrdinalIgnoreCase))
            {
                key = pair.Key;
                entry = pair.Value;
                return true;
            }
        }
        key = string.Empty;
        entry = null!;
        return false;
    }

    private static PropertyInfo GetProperty(Entry entry)
        => typeof(AppSettings).GetProperty(entry.Property, BindingFlags.Instance | BindingFlags.Public)
           ?? throw new InvalidOperationException($"Missing setting property: {entry.Property}");

    private static bool TryConvert(Entry entry, string raw, out object? value, out string? error)
    {
        var type = GetProperty(entry).PropertyType;
        var target = Nullable.GetUnderlyingType(type) ?? type;
        error = null;
        if (target == typeof(bool))
        {
            if (TryParseBool(raw, out var b)) { value = b; return true; }
            value = null;
            error = $"Invalid boolean value: {raw}. Use true/false, on/off, yes/no or 1/0.";
            return false;
        }

        if (target == typeof(int))
        {
            if (!int.TryParse(raw, out var n))
            {
                value = null;
                error = $"Invalid integer value: {raw}";
                return false;
            }
            if (entry.Min.HasValue && n < entry.Min.Value || entry.Max.HasValue && n > entry.Max.Value)
            {
                value = null;
                error = $"Value must be between {entry.Min} and {entry.Max}.";
                return false;
            }
            value = n;
            return true;
        }

        if (target == typeof(string))
        {
            if (entry.Allowed != null)
            {
                var canonical = entry.Allowed.FirstOrDefault(v => string.Equals(v, raw, StringComparison.OrdinalIgnoreCase));
                if (canonical == null)
                {
                    value = null;
                    error = $"Allowed values: {string.Join(", ", entry.Allowed)}";
                    return false;
                }
                value = canonical;
                return true;
            }
            value = raw.Equals("null", StringComparison.OrdinalIgnoreCase) && Nullable.GetUnderlyingType(type) != null ? null : raw;
            return true;
        }
        value = null;
        error = $"Unsupported setting type: {target.Name}";
        return false;
    }

    private static bool TryParseBool(string raw, out bool value)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "true": case "1": case "yes": case "on": value = true; return true;
            case "false": case "0": case "no": case "off": value = false; return true;
            default: value = false; return false;
        }
    }

    private static string FormatValue(object? value)
        => value switch
        {
            null => "",
            bool b => b ? "true" : "false",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
        };
}
