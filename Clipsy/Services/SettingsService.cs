using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Clipsy.Services;

public sealed class AppSettings
{
    public int SettingsVersion { get; set; } = SettingsService.CurrentSettingsVersion;

    // General
    public string Language { get; set; } = "auto";          // auto / en / ru
    public string Theme { get; set; } = "auto";             // auto / dark / light
    public string OcrEngine { get; set; } = "PPOCRv5";        // PPOCRv5 / WinRT / Tesseract
    public bool OcrEngineOfferShown { get; set; }
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
    // New key: the old experimental one was saved as false everywhere.
    public bool ModernScreenshotCapture { get; set; } = true;
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
    public string HotkeyQuickScreenshot { get; set; } = "";  // screen under cursor, no overlay

    // Updates
    public DateTime LastUpdateCheckUtc { get; set; } = DateTime.MinValue;
    public string SkippedVersion { get; set; } = string.Empty;

    // Translation
    public string TranslateService { get; set; } = "Bing"; // Bing / Google / MyMemory
    public string TranslateFrom { get; set; } = "auto";
    public string TranslateTo { get; set; } = "ui"; // "ui" = current interface language
    public bool TranslationNoticeShown { get; set; }
    // Separate from TranslationNoticeShown: that one was also set when the old notice was
    // only a toast (or hidden), so it doesn't mean the user agreed.
    public bool TranslationConsented { get; set; }

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

    private static readonly System.Reflection.PropertyInfo[] Properties =
        typeof(AppSettings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite).ToArray();

    /// <summary>Copies onto this instance only the properties that differ between baseline and edited.</summary>
    public void ApplyChanges(AppSettings baseline, AppSettings edited)
    {
        foreach (var p in Properties)
        {
            var after = p.GetValue(edited);
            if (!Equals(p.GetValue(baseline), after)) p.SetValue(this, after);
        }
    }

    /// <summary>Defaults for every preference, keeping runtime state (last folders, mic state,
    /// update stamps, one-time offers and notices) so a reset doesn't re-prompt or forget.</summary>
    internal static AppSettings DefaultsKeepingState(AppSettings current) => new()
    {
        OcrEngineOfferShown = current.OcrEngineOfferShown,
        LastScreenshotFolder = current.LastScreenshotFolder,
        LastVideoFolder = current.LastVideoFolder,
        LastChangelogVersion = current.LastChangelogVersion,
        MicrophoneMuted = current.MicrophoneMuted,
        MicrophoneStateInitialized = current.MicrophoneStateInitialized,
        LastUpdateCheckUtc = current.LastUpdateCheckUtc,
        SkippedVersion = current.SkippedVersion,
        TranslationNoticeShown = current.TranslationNoticeShown,
        TranslationConsented = current.TranslationConsented,
        ProEnabled = current.ProEnabled,
    };

    /// <summary>Reads known fields one by one onto defaults: a wrongly-typed field is
    /// skipped instead of failing the whole file.</summary>
    internal static AppSettings FromJson(JsonObject root, JsonSerializerOptions options)
    {
        var s = new AppSettings();
        foreach (var p in Properties)
        {
            if (!root.TryGetPropertyValue(p.Name, out var node)) continue;
            try
            {
                if (node is null)
                {
                    if (!p.PropertyType.IsValueType) p.SetValue(s, null);
                    continue;
                }
                p.SetValue(s, node.Deserialize(p.PropertyType, options));
            }
            catch (Exception ex)
            {
                Diagnostics.Log($"Settings field '{p.Name}' ignored: {ex.Message}");
            }
        }
        return s;
    }

    internal void FillNullStrings()
    {
        var defaults = new AppSettings();
        foreach (var p in Properties)
        {
            if (p.PropertyType != typeof(string) || p.GetValue(this) != null) continue;
            var fallback = p.GetValue(defaults);
            if (fallback != null) p.SetValue(this, fallback);
        }
    }
}

public sealed class SettingsService
{
    public const int CurrentSettingsVersion = 2;
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    private readonly string _path;
    private readonly string _backupPath;
    private readonly string _tempPath;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    private readonly object _saveLock = new();
    // File writes are serialized here; a newer snapshot is never overwritten by an older one.
    private readonly object _writeLock = new();
    private long _snapshotSeq;
    private long _writtenSeq;
    private (byte[] Bytes, long Seq)? _pendingState;
    private bool _stateWriterQueued;
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
            if (JsonNode.Parse(json) is not JsonObject root) return false;
            if (root[nameof(AppSettings.SettingsVersion)] is JsonValue v)
                v.TryGetValue(out version);
            if (version > CurrentSettingsVersion)
            {
                // Written by a newer build: keep a copy so a downgrade doesn't silently lose it.
                var keep = path + $".v{version}";
                if (!File.Exists(keep)) File.Copy(path, keep);
                Diagnostics.Log($"Settings '{Path.GetFileName(path)}' has newer version {version}; loading known fields.");
            }
            settings = AppSettings.FromJson(root, _json);
            return true;
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
                // MyMemory was the old default and translates worst; move it to the new default once.
                case 1:
                    if (string.Equals(s.TranslateService, "MyMemory", StringComparison.OrdinalIgnoreCase)) s.TranslateService = "Bing";
                    version = 2;
                    break;
                default: version = CurrentSettingsVersion; break;
            }
        }
        s.SettingsVersion = CurrentSettingsVersion;
        Normalize(s);
        return s;
    }

    private static void Normalize(AppSettings s)
    {
        s.FillNullStrings();
        s.Language = OneOf(s.Language, "auto", "auto", "en", "ru");
        s.Theme = OneOf(s.Theme, "auto", "auto", "dark", "light");
        s.OcrEngine = OneOf(s.OcrEngine, "PPOCRv5", "WinRT", "Tesseract", "PPOCRv5");
        s.TranslateService = OneOf(s.TranslateService, "Bing", "Bing", "Google", "MyMemory");
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

        // A binding that is no longer allowed (Esc, Win, bare typing keys) falls back to its default.
        var d = new AppSettings();
        s.HotkeyCapture          = ValidHotkey(s.HotkeyCapture,          d.HotkeyCapture,          global: true,  optional: false);
        s.HotkeyRecordSilentSave = ValidHotkey(s.HotkeyRecordSilentSave, d.HotkeyRecordSilentSave, global: true,  optional: true);
        s.HotkeyMicToggle        = ValidHotkey(s.HotkeyMicToggle,        d.HotkeyMicToggle,        global: true,  optional: true);
        s.HotkeyQuickScreenshot  = ValidHotkey(s.HotkeyQuickScreenshot,  d.HotkeyQuickScreenshot,  global: true,  optional: true);
        s.HotkeyScreenshotSilent = ValidHotkey(s.HotkeyScreenshotSilent, d.HotkeyScreenshotSilent, global: false, optional: true);
        s.HotkeyCopy             = ValidHotkey(s.HotkeyCopy,             d.HotkeyCopy,             global: false, optional: true);
        s.HotkeyUndo             = ValidHotkey(s.HotkeyUndo,             d.HotkeyUndo,             global: false, optional: true);
        s.HotkeyRedo             = ValidHotkey(s.HotkeyRedo,             d.HotkeyRedo,             global: false, optional: true);
        s.HotkeySelectAll        = ValidHotkey(s.HotkeySelectAll,        d.HotkeySelectAll,        global: false, optional: true);
        s.HotkeySelectMonitor    = ValidHotkey(s.HotkeySelectMonitor,    d.HotkeySelectMonitor,    global: false, optional: true);
        if (HotkeyService.IsReservedKey(s.EyedropperModifier)) s.EyedropperModifier = d.EyedropperModifier;
    }

    private static string ValidHotkey(string value, string fallback, bool global, bool optional)
    {
        if (string.IsNullOrWhiteSpace(value)) return optional ? string.Empty : fallback;
        if (HotkeyService.IsValidBinding(value, global)) return value;
        Diagnostics.Log($"Hotkey '{value}' is not allowed; reset to '{fallback}'.");
        return fallback;
    }

    // Returns the canonical spelling, so "PNG" is stored and compared as "png".
    private static string OneOf(string? value, string fallback, params string[] allowed)
        => allowed.FirstOrDefault(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase)) ?? fallback;

    /// <summary>Persists and notifies subscribers (hotkeys, theme, language, open windows).</summary>
    public bool Save()
    {
        var bytes = Snapshot(out long seq);
        if (bytes == null || !WriteSnapshot(bytes, seq)) return false;
        try { SettingsChanged?.Invoke(); }
        catch (Exception ex) { Diagnostics.Log("SettingsChanged subscriber failed", ex); }
        return true;
    }

    /// <summary>Persists runtime state (last folder, mic, update stamps) without rebuilding the UI.
    /// Serialized here, written on a background thread; bursts coalesce into one write.</summary>
    public bool SaveState()
    {
        var bytes = Snapshot(out long seq);
        if (bytes == null) return false;
        lock (_saveLock)
        {
            _pendingState = (bytes, seq);
            if (_stateWriterQueued) return true;
            _stateWriterQueued = true;
        }
        Task.Run(WritePendingState);
        return true;
    }

    /// <summary>Synchronously writes state queued by SaveState (call before exit).</summary>
    public void Flush() => WritePendingState();

    private void WritePendingState()
    {
        // Held across the take so Flush also waits out a background write already in progress.
        lock (_writeLock)
        {
            (byte[] Bytes, long Seq)? next;
            lock (_saveLock)
            {
                next = _pendingState;
                _pendingState = null;
                _stateWriterQueued = false;
            }
            if (next is { } p) WriteSnapshot(p.Bytes, p.Seq);
        }
    }

    private byte[]? Snapshot(out long seq)
    {
        lock (_saveLock)
        {
            Normalize(Settings);
            Settings.SettingsVersion = CurrentSettingsVersion;
            seq = ++_snapshotSeq;
            try { return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Settings, _json)); }
            catch (Exception ex)
            {
                Diagnostics.Log("Settings serialize failed", ex);
                return null;
            }
        }
    }

    private bool WriteSnapshot(byte[] bytes, long seq)
    {
        lock (_writeLock)
        {
            if (seq <= _writtenSeq) return true; // a newer snapshot is already on disk
            try
            {
                using (var fs = new FileStream(_tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(flushToDisk: true);
                }
                if (IsReadableJson(_path)) File.Copy(_path, _backupPath, overwrite: true);
                File.Move(_tempPath, _path, overwrite: true);
                _writtenSeq = seq;
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Settings save failed", ex);
                return false;
            }
            finally
            {
                try { if (File.Exists(_tempPath)) File.Delete(_tempPath); } catch { }
            }
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

    public bool Replace(AppSettings updated)
    {
        var previous = Settings;
        Settings = updated;
        if (Save()) return true;
        Settings = previous;
        return false;
    }

    public bool ResetToDefaults()
    {
        var previous = Settings;
        Settings = AppSettings.DefaultsKeepingState(previous);
        if (Save()) return true;
        Settings = previous;
        return false;
    }

    public string GetEffectiveVideoFolder()
    {
        if (Settings.RememberLastFolder && !string.IsNullOrEmpty(Settings.LastVideoFolder)
            && Directory.Exists(Settings.LastVideoFolder))
        {
            return Settings.LastVideoFolder!;
        }
        var configured = Settings.VideoFolder;
        if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured)) return configured!;
        var fallback = string.IsNullOrEmpty(configured) ? DefaultVideoFolder : configured!;
        try { Directory.CreateDirectory(fallback); }
        catch { fallback = DefaultVideoFolder; Directory.CreateDirectory(fallback); }
        return fallback;
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
        var fallback = string.IsNullOrEmpty(configured) ? DefaultScreenshotFolder : configured!;
        try { Directory.CreateDirectory(fallback); }
        catch { fallback = DefaultScreenshotFolder; Directory.CreateDirectory(fallback); }
        return fallback;
    }
}
