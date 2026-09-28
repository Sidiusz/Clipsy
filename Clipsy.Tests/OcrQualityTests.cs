using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text;
using Clipsy.Services;
using Xunit;
using Xunit.Abstractions;

namespace Clipsy.Tests;

/// <summary>OCR regression corpus: synthetic renders (exact ground truth) plus real screenshots in
/// OcrSamples/. Reports character error rate per engine and fails when it exceeds the recorded ceiling.
/// PP-OCRv5 cases need the models in %LOCALAPPDATA%\Clipsy\ppocrv5 and are skipped otherwise.</summary>
[Trait("Category", "Ocr")]
public class OcrQualityTests(ITestOutputHelper output)
{
    public sealed record Sample(string Name, byte[] Png, string Expected);

    private static readonly string SampleDir = Path.Combine(AppContext.BaseDirectory, "OcrSamples");

    public static IEnumerable<Sample> Samples()
    {
        yield return Synthetic("ui_en_light_14", "Segoe UI", 14, dark: false,
            "Settings saved: 3 files, 2 folders.",
            "Hello, world! Price: $10.99 (incl. tax)",
            "Open the folder and press Ctrl+S to save.");
        yield return Synthetic("ui_ru_dark_13", "Segoe UI", 13, dark: true,
            "Настройки сохранены.",
            "Открыть папку со скриншотами",
            "Запись экрана остановлена с ошибкой.");
        yield return Synthetic("ui_mixed_light_12", "Segoe UI", 12, dark: false,
            "Download PP-OCRv5 модель (7.5 MB)",
            "Версия 1.0.6 доступна: click to update");
        yield return Synthetic("ui_en_dark_11", "Segoe UI", 11, dark: true,
            "Capture screen    PrtSc",
            "Open screenshots folder",
            "Exit");
        yield return Synthetic("cjk_zh_16", "Microsoft YaHei", 16, dark: false,
            "你好，世界。今天天气很好。",
            "设置已保存");
        foreach (var png in Directory.Exists(SampleDir) ? Directory.GetFiles(SampleDir, "*.png") : Array.Empty<string>())
        {
            var txt = Path.ChangeExtension(png, ".txt");
            if (File.Exists(txt))
                yield return new Sample(Path.GetFileNameWithoutExtension(png), File.ReadAllBytes(png), File.ReadAllText(txt));
        }
    }

    private static Sample Synthetic(string name, string font, float px, bool dark, params string[] lines)
    {
        using var f = new Font(font, px, FontStyle.Regular, GraphicsUnit.Pixel);
        int lineH = (int)Math.Ceiling(px * 1.9);
        using var measure = new Bitmap(1, 1);
        int width;
        using (var g = Graphics.FromImage(measure))
            width = (int)Math.Ceiling(lines.Max(l => g.MeasureString(l, f).Width)) + 40;
        using var bmp = new Bitmap(width, lineH * lines.Length + 24, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(dark ? Color.FromArgb(0x1E, 0x1F, 0x22) : Color.FromArgb(0xF3, 0xF3, 0xF3));
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using var brush = new SolidBrush(dark ? Color.FromArgb(0xE6, 0xE6, 0xE6) : Color.FromArgb(0x1B, 0x1B, 0x1B));
            for (int i = 0; i < lines.Length; i++)
                g.DrawString(lines[i], f, brush, 16, 12 + i * lineH);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return new Sample(name, ms.ToArray(), string.Join("\n", lines));
    }

    // PP-OCRv5 ceilings (en+ru Windows OCR hints, all models installed). Windows OCR alone depends on
    // the machine's language packs, so it's only reported. Lower as OCR improves; never raise to pass.
    private static readonly Dictionary<(string Engine, string Sample), double> MaxCer = new()
    {
        [("PPOCRv5", "ui_en_light_14")] = 0.02,
        [("PPOCRv5", "ui_ru_dark_13")] = 0.02,
        [("PPOCRv5", "ui_mixed_light_12")] = 0.20,
        [("PPOCRv5", "ui_en_dark_11")] = 0.08,
        [("PPOCRv5", "cjk_zh_16")] = 0.01,
        [("PPOCRv5", "chat_ru_en_16px")] = 0.015,
        [("PPOCRv5", "mixed_ru_en_dark")] = 0.03,
    };

    [Fact]
    public async Task CharacterErrorRateStaysWithinCeilings()
    {
        var engines = new List<(string Name, IOcrEngine Engine)> { ("WinRT", new WinRtOcrEngine()) };
        if (PpOcrV5Service.IsReady) engines.Add(("PPOCRv5", new PpOcrV5Engine()));

        var failures = new List<string>();
        foreach (var sample in Samples())
        {
            foreach (var (name, engine) in engines)
            {
                string actual;
                try { actual = OcrTextLayout.BuildText(await engine.RecognizeAsync(sample.Png)); }
                catch (OcrUnavailableException ex) { output.WriteLine($"{name,-8} {sample.Name,-22} skipped: {ex.Message}"); continue; }
                double cer = Cer(sample.Expected, actual);
                output.WriteLine($"{name,-8} {sample.Name,-22} CER={cer:P1}");
                if (cer > 0) output.WriteLine("    got: " + actual.Replace("\n", " ⏎ "));
                if (MaxCer.TryGetValue((name, sample.Name), out var max) && cer > max + 1e-9)
                    failures.Add($"{name}/{sample.Name}: CER {cer:P1} > {max:P1}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    internal static double Cer(string expected, string actual)
    {
        static string Norm(string s)
        {
            var sb = new StringBuilder();
            foreach (var line in s.Replace("\r", "").Split('\n'))
            {
                var t = string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                if (t.Length > 0) sb.Append(t).Append('\n');
            }
            return sb.ToString().TrimEnd();
        }
        var a = Norm(expected);
        var b = Norm(actual);
        if (a.Length == 0) return b.Length == 0 ? 0 : 1;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return (double)prev[b.Length] / a.Length;
    }
}
