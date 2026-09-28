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
        Assert.Equal(0, raised);
        Assert.True(svc.Save());
        Assert.Equal(1, raised);
    }
}
