using System.IO;
using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clipsy-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void ApplyChangesCopiesOnlyEditedFields()
    {
        var baseline = new AppSettings { ScreenshotFormat = "png", LastScreenshotFolder = @"C:\old" };
        var edited = baseline.Clone();
        edited.ScreenshotFormat = "jpg";

        var live = baseline.Clone();
        live.LastScreenshotFolder = @"D:\new";
        live.ApplyChanges(baseline, edited);

        Assert.Equal("jpg", live.ScreenshotFormat);
        Assert.Equal(@"D:\new", live.LastScreenshotFolder);
    }

    [Fact]
    public void NullStringsAreRestoredToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            "{\"SettingsVersion\":1,\"TesseractLanguages\":null,\"HotkeyCapture\":null,\"ScreenshotFolder\":null}");

        var svc = new SettingsService(_dir);

        Assert.Equal("", svc.Settings.TesseractLanguages);
        Assert.Equal("PrintScreen", svc.Settings.HotkeyCapture);
        Assert.Null(svc.Settings.ScreenshotFolder);
    }

    [Theory]
    [InlineData("MyMemory", "Bing")]
    [InlineData("Google", "Google")]
    public void Version1TranslateServiceMigrates(string saved, string expected)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), $"{{\"SettingsVersion\":1,\"TranslateService\":\"{saved}\"}}");

        var svc = new SettingsService(_dir);

        Assert.Equal(expected, svc.Settings.TranslateService);
    }

    [Fact]
    public void NewerSettingsFileIsLoadedAndPreserved()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{\"SettingsVersion\":99,\"ScreenshotFormat\":\"webp\",\"FutureOnly\":1}");

        var svc = new SettingsService(_dir);

        Assert.Equal("webp", svc.Settings.ScreenshotFormat);
        Assert.True(File.Exists(path + ".v99"));
    }

    [Fact]
    public void SaveStateDoesNotRaiseSettingsChanged()
    {
        var svc = new SettingsService(_dir);
        int raised = 0;
        svc.SettingsChanged += () => raised++;

        Assert.True(svc.SaveState());
        svc.Flush();
        Assert.Equal(0, raised);
        Assert.True(svc.Save());
        Assert.Equal(1, raised);
    }

    [Fact]
    public void SaveStateIsWrittenByFlush()
    {
        var svc = new SettingsService(_dir);
        svc.Settings.SkippedVersion = "9.9.9";
        Assert.True(svc.SaveState());
        svc.Flush();

        Assert.Equal("9.9.9", new SettingsService(_dir).Settings.SkippedVersion);
    }

    [Fact]
    public void DisallowedHotkeysFallBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            "{\"SettingsVersion\":2,\"HotkeyCapture\":\"A\",\"HotkeyCopy\":\"Escape\",\"HotkeyMicToggle\":\"Win+M\",\"HotkeyRecordSilentSave\":\"F9\",\"HotkeyUndo\":\"\"}");

        var s = new SettingsService(_dir).Settings;

        Assert.Equal("PrintScreen", s.HotkeyCapture);
        Assert.Equal("Ctrl+C", s.HotkeyCopy);
        Assert.Equal("", s.HotkeyMicToggle);
        Assert.Equal("F9", s.HotkeyRecordSilentSave);
        Assert.Equal("", s.HotkeyUndo);
    }

    [Fact]
    public void WronglyTypedFieldDoesNotDiscardTheFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            "{\"SettingsVersion\":2,\"JpgQuality\":\"high\",\"ScreenshotFormat\":\"PNG\",\"VideoCodec\":\"h.265\",\"SkippedVersion\":\"1.2.3\"}");

        var s = new SettingsService(_dir).Settings;

        Assert.Equal(90, s.JpgQuality);
        Assert.Equal("png", s.ScreenshotFormat);
        Assert.Equal("H.265", s.VideoCodec);
        Assert.Equal("1.2.3", s.SkippedVersion);
    }

    [Fact]
    public void ResetToDefaultsKeepsRuntimeState()
    {
        var svc = new SettingsService(_dir);
        svc.Settings.ScreenshotFormat = "jpg";
        svc.Settings.SkippedVersion = "2.0.0";
        svc.Settings.LastScreenshotFolder = @"C:\shots";
        svc.Settings.OcrEngineOfferShown = true;
        svc.Settings.TranslationNoticeShown = true;

        Assert.True(svc.ResetToDefaults());

        Assert.Equal("png", svc.Settings.ScreenshotFormat);
        Assert.Equal("2.0.0", svc.Settings.SkippedVersion);
        Assert.Equal(@"C:\shots", svc.Settings.LastScreenshotFolder);
        Assert.True(svc.Settings.OcrEngineOfferShown);
        Assert.True(svc.Settings.TranslationNoticeShown);
    }
}
