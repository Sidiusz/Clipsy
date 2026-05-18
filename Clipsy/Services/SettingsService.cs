using System;
using System.IO;
using System.Text.Json;

namespace Clipsy.Services;

public sealed class AppSettings
{
    public string? ScreenshotFolder { get; set; }
    public string? VideoFolder { get; set; }
    public bool RememberLastFolder { get; set; } = true;
    public string? LastScreenshotFolder { get; set; }
    public string? LastVideoFolder { get; set; }
    public string OcrEngine { get; set; } = "Tesseract";
    public string Language { get; set; } = "auto";
    public string Theme { get; set; } = "auto";
}

/// <summary>
/// Persists user settings as JSON at %LOCALAPPDATA%\Clipsy\settings.json.
/// Reads on demand; writes synchronously when Save() is called.
/// </summary>
public sealed class SettingsService
{
    private static readonly Lazy<SettingsService> _instance = new(() => new SettingsService());
    public static SettingsService Instance => _instance.Value;

    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };
    public AppSettings Settings { get; private set; }

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
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Settings save failed: {ex.Message}");
        }
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
