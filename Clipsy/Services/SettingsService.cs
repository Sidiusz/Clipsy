using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Clipsy.Services;

public sealed class AppSettings
{
    public int SettingsVersion { get; set; } = SettingsService.CurrentSettingsVersion;

    // General
    public string Language { get; set; } = "auto";          // auto / en / ru
    public string Theme { get; set; } = "auto";             // auto / dark / light
    public string OcrEngine { get; set; } = "WinRT";          // Tesseract / WinRT
    public string TesseractLanguages { get; set; } = "";   // comma-separated codes, e.g. "eng,rus"
    public string? ScreenshotFolder { get; set; }
    public string? VideoFolder { get; set; }
    public bool RememberLastFolder { get; set; } = true;
    public string? LastScreenshotFolder { get; set; }
    public string? LastVideoFolder { get; set; }
    public string UpdateInterval { get; set; } = "daily";   // hourly / daily / weekly / monthly / never
    public bool AutoDownloadUpdates { get; set; } = true;   // download installer when PC is idle
    public string LastChangelogVersion { get; set; } = "";  // version whose changelog was auto-shown
    public string AfterSaveAction { get; set; } = "nothing"; // open-file / open-folder / nothing

    // Screenshot
    public string ScreenshotFormat { get; set; } = "png";   // png / jpg / webp
    public int JpgQuality { get; set; } = 90;               // 50..100

    // Video
    public string VideoFormat { get; set; } = "mp4";        // mp4 / avi / mkv / gif
    public string VideoCodec { get; set; } = "H.264";       // H.264 / H.265 / VP9 / AV1
    public string VideoResolution { get; set; } = "1080p";  // 480p / 720p / 1080p / 1440p / Original
    public int VideoFramerate { get; set; } = 60;           // 15 / 30 / 60 / 0 = native (display refresh rate)
    public int VideoBitrateMbps { get; set; } = 8;

    // Microphone
    public bool MicrophoneEnabled { get; set; } = true;
    public bool MicrophoneMuted { get; set; } = true;
    // False for settings created before persistent mic-state semantics existed.
    // The first recording initializes them safely to muted.
    public bool MicrophoneStateInitialized { get; set; } = false;
    public string MicrophoneDevice { get; set; } = "";       // empty = default system device (DeviceName from Recorder.GetSystemAudioDevices)
    public string HotkeyMicToggle { get; set; } = "";        // empty = disabled

    // Cursor capture
    public bool CaptureScreenshotCursor { get; set; } = false;
    public bool ExperimentalModernScreenshotCapture { get; set; } = false;
    public bool CaptureVideoCursor { get; set; } = true;

    // Capture overlay: dock toolbars to the corner where the selection drag ended
    public bool DynamicToolbarIslands { get; set; } = false;

    // Hold this modifier while a draw tool is active to temporarily eyedrop.
    public string EyedropperModifier { get; set; } = "Alt"; // Alt / Ctrl / Shift / VirtualKey name
    public bool CopyEyedropperHexToClipboard { get; set; } = false;

    // GIF
    public int GifColors { get; set; } = 256;
    public int GifFps { get; set; } = 12;
    public bool GifDither { get; set; } = true;

    // Hotkeys (stored as human-readable accelerator strings)
    public string HotkeyCapture { get; set; } = "PrintScreen";
    public string HotkeyScreenshotSilent { get; set; } = "Ctrl+S";
    public string HotkeyCopy { get; set; } = "Ctrl+C";
    public string HotkeyUndo { get; set; } = "Ctrl+Z";
    public string HotkeyRedo { get; set; } = "Ctrl+Y";
    public string HotkeySelectAll { get; set; } = "Ctrl+A";
    public string HotkeySelectMonitor { get; set; } = "Ctrl+B";
    public string HotkeyRecordSilentSave { get; set; } = ""; // disabled by default

    // Updates
    public DateTime LastUpdateCheckUtc { get; set; } = DateTime.MinValue;
    public string SkippedVersion { get; set; } = string.Empty;

    // Translation
    public string TranslateService { get; set; } = "Google"; // Google / MyMemory — Google default for better quality + language coverage.
    public string TranslateFrom { get; set; } = "auto";
    public string TranslateTo { get; set; } = "ui"; // "ui" = current interface language

    // Notifications
    public bool NotificationsEnabled { get; set; } = true;
    public int NotificationDurationSeconds { get; set; } = 5;
    public bool NotifyScreenshotSaved { get; set; } = true;
    public bool NotifyVideoSaved { get; set; } = true;
    public bool NotifyClipboard { get; set; } = true;
    public bool NotifyErrors { get; set; } = true;
    public bool NotifyUpdateAvailable { get; set; } = true;
    public bool NotifyHints { get; set; } = true;

    // Pro v1 stubs
    public bool ProEnabled { get; set; } = false;

    public AppSettings Clone()
    {
        return (AppSettings)MemberwiseClone();
    }
}

public sealed class SettingsService
{
    public const int CurrentSettingsVersion = 1;
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    private readonly string _path;
    private readonly string _backupPath;
    private readonly string _tempPath;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public AppSettings Settings { get; private set; }

    public event Action? SettingsChanged;

    public string DefaultScreenshotFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Clipsy", "Screenshots");

    public string DefaultVideoFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Clipsy", "Video");

    private SettingsService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipsy"))
    {
    }

    internal SettingsService(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
        _backupPath = _path + ".bak";
        _tempPath = _path + ".tmp";
        Settings = Load();
    }

    private AppSettings Load()
    {
        if (TryLoadFile(_path, out var settings, out var version))
            return PrepareLoaded(settings!, version);
        if (TryLoadFile(_backupPath, out settings, out version))
        {
            Diagnostics.Log("Settings primary invalid; loaded backup.");
            return PrepareLoaded(settings!, version);
        }
        return new AppSettings();
    }

    private bool TryLoadFile(string path, out AppSettings? settings, out int version)
    {
        settings = null;
        version = 0;
        try
        {
            if (!File.Exists(path)) return false;
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(nameof(AppSettings.SettingsVersion), out var v))
                v.TryGetInt32(out version);
            if (version > CurrentSettingsVersion) return false;
            settings = JsonSerializer.Deserialize<AppSettings>(json, _json);
            return settings != null;
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"Settings load failed for '{Path.GetFileName(path)}'", ex);
            return false;
        }
    }

    private static AppSettings PrepareLoaded(AppSettings s, int version)
    {
        while (version < CurrentSettingsVersion)
        {
            switch (version)
            {
                case 0: version = 1; break;
                default: version = CurrentSettingsVersion; break;
            }
        }
        s.SettingsVersion = CurrentSettingsVersion;
        Normalize(s);
        return s;
    }

    private static void Normalize(AppSettings s)
    {
        s.Language = OneOf(s.Language, "auto", "auto", "en", "ru");
        s.Theme = OneOf(s.Theme, "auto", "auto", "dark", "light");
        s.ScreenshotFormat = OneOf(s.ScreenshotFormat, "png", "png", "jpg", "webp");
        s.VideoFormat = OneOf(s.VideoFormat, "mp4", "mp4", "avi", "mkv", "gif");
        s.VideoCodec = OneOf(s.VideoCodec, "H.264", "H.264", "H.265", "VP9", "AV1");
        s.VideoResolution = OneOf(s.VideoResolution, "1080p", "480p", "720p", "1080p", "1440p", "Original");
        s.UpdateInterval = OneOf(s.UpdateInterval, "daily", "hourly", "daily", "weekly", "monthly", "never");
        s.AfterSaveAction = OneOf(s.AfterSaveAction, "nothing", "open-file", "open-folder", "nothing");
        s.JpgQuality = Math.Clamp(s.JpgQuality, 50, 100);
        s.VideoFramerate = s.VideoFramerate == 0 ? 0 : Math.Clamp(s.VideoFramerate, 10, 240);
        s.VideoBitrateMbps = Math.Clamp(s.VideoBitrateMbps, 1, 50);
        s.GifColors = Math.Clamp(s.GifColors, 16, 256);
        s.GifFps = Math.Clamp(s.GifFps, 5, 30);
        s.NotificationDurationSeconds = Math.Clamp(s.NotificationDurationSeconds, 1, 30);
    }

    private static string OneOf(string? value, string fallback, params string[] allowed)
        => allowed.Contains(value ?? string.Empty, StringComparer.OrdinalIgnoreCase) ? value! : fallback;

    public void Save()
    {
        Normalize(Settings);
        Settings.SettingsVersion = CurrentSettingsVersion;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Settings, _json));
            using (var fs = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            if (IsReadableJson(_path)) File.Copy(_path, _backupPath, overwrite: true);
            File.Move(_tempPath, _path, overwrite: true);
            SettingsChanged?.Invoke();
        }
        catch (Exception ex) { Diagnostics.Log("Settings save failed", ex); }
        finally
        {
            try { if (File.Exists(_tempPath)) File.Delete(_tempPath); } catch { }
        }
    }

    private static bool IsReadableJson(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch { return false; }
    }

    public void Replace(AppSettings updated)
    {
        Settings = updated;
        Save();
    }

    public void ResetToDefaults()
    {
        Settings = new AppSettings();
        Save();
    }

    public string GetEffectiveScreenshotFolder()
    {
        if (Settings.RememberLastFolder && !string.IsNullOrEmpty(Settings.LastScreenshotFolder)
            && Directory.Exists(Settings.LastScreenshotFolder))
        {
            return Settings.LastScreenshotFolder!;
        }
        var configured = Settings.ScreenshotFolder;
        if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured)) return configured!;
        var fallback = DefaultScreenshotFolder;
        Directory.CreateDirectory(fallback);
        return fallback;
    }
}
