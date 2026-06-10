using System;
using System.IO;
using System.Text.Json;

namespace Clipsy.Services;

public sealed class AppSettings
{
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
    public string AfterSaveAction { get; set; } = "nothing"; // open-file / open-folder / nothing

    // Screenshot
    public string ScreenshotFormat { get; set; } = "png";   // png / jpg / webp
    public int JpgQuality { get; set; } = 90;               // 50..100

    // Video
    public string VideoFormat { get; set; } = "mp4";        // mp4 / avi / mkv / gif
    public string VideoCodec { get; set; } = "H.264";       // H.264 / H.265 / VP9 / AV1
    public string VideoResolution { get; set; } = "1080p";  // 480p / 720p / 1080p / 1440p / Original
    public int VideoBitrateMbps { get; set; } = 8;

    // Microphone
    public bool MicrophoneEnabled { get; set; } = false;
    public bool MicrophoneMuted { get; set; } = false;
    public string MicrophoneDevice { get; set; } = "";       // empty = default system device (DeviceName from Recorder.GetSystemAudioDevices)
    public string HotkeyMicToggle { get; set; } = "";        // empty = disabled

    // Cursor capture
    public bool CaptureScreenshotCursor { get; set; } = true;
    public bool CaptureVideoCursor { get; set; } = true;

    // Capture overlay: dock toolbars to the corner where the selection drag ended
    public bool DynamicToolbarIslands { get; set; } = false;

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
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public AppSettings Settings { get; private set; }

    public event Action? SettingsChanged;

    public string DefaultScreenshotFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Clipsy", "Screenshots");

    public string DefaultVideoFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Clipsy", "Video");

    private SettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Clipsy");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        Settings = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) return s;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Settings load failed: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(Settings, _json));
            SettingsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Settings save failed: {ex.Message}");
        }
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
