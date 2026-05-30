using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Clipsy.Services;

public sealed record TessdataLang(string Code, string DisplayName, string ApproxSize);

public static class TessdataService
{
    // tessdata_best: larger but markedly more accurate LSTM models — fewer
    // mixed-script misreads (e.g. Cyrillic "Видео" no longer flips to Latin).
    private const string BaseUrl = "https://github.com/tesseract-ocr/tessdata_best/raw/main/";

    public static readonly string StorageDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clipsy", "tessdata");

    public static readonly IReadOnlyList<TessdataLang> Catalog = new TessdataLang[]
    {
        new("eng",     "English",                        "~12 MB"),
        new("rus",     "Russian / Русский",               "~14 MB"),
        new("deu",     "German / Deutsch",               "~15 MB"),
        new("fra",     "French / Français",              "~13 MB"),
        new("spa",     "Spanish / Español",              "~14 MB"),
        new("ita",     "Italian / Italiano",             "~13 MB"),
        new("por",     "Portuguese / Português",         "~13 MB"),
        new("pol",     "Polish / Polski",                "~14 MB"),
        new("nld",     "Dutch / Nederlands",             "~13 MB"),
        new("tur",     "Turkish / Türkçe",               "~14 MB"),
        new("ukr",     "Ukrainian / Українська",         "~14 MB"),
        new("chi_sim", "Chinese Simplified / 简体中文",  "~44 MB"),
        new("chi_tra", "Chinese Traditional / 繁體中文", "~48 MB"),
        new("jpn",     "Japanese / 日本語",              "~37 MB"),
        new("kor",     "Korean / 한국어",                "~16 MB"),
        new("ara",     "Arabic / العربية",               "~15 MB"),
    };

    public static (int Min, int Max) ApproxSizeRangeMb()
    {
        var nums = Catalog
            .Select(c => System.Text.RegularExpressions.Regex.Match(c.ApproxSize, @"\d+"))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Value))
            .ToList();
        return nums.Count == 0 ? (0, 0) : (nums.Min(), nums.Max());
    }

    public static bool IsInstalled(string code)
        => File.Exists(Path.Combine(StorageDir, code + ".traineddata"));

    public static IReadOnlyList<string> InstalledSelectedCodes()
    {
        return SettingsService.Instance.Settings.TesseractLanguages
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsInstalled)
            .ToList();
    }

    public static async Task DownloadAsync(string code, IProgress<int> progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(StorageDir);
        var tmp = Path.Combine(StorageDir, code + ".traineddata.tmp");
        var final = Path.Combine(StorageDir, code + ".traineddata");

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        using var response = await client.GetAsync(BaseUrl + code + ".traineddata",
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? -1;
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using (var file = File.Create(tmp))
        {
            var buffer = new byte[65536];
            long downloaded = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await file.WriteAsync(buffer, 0, read, ct);
                downloaded += read;
                if (total > 0) progress?.Report((int)(downloaded * 100 / total));
            }
        }

        if (File.Exists(final)) File.Delete(final);
        File.Move(tmp, final);
        progress?.Report(100);
    }

    public static void Delete(string code)
    {
        var path = Path.Combine(StorageDir, code + ".traineddata");
        if (File.Exists(path)) File.Delete(path);
    }
}
