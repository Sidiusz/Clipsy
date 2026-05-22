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
    private const string BaseUrl = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/";

    public static readonly string StorageDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clipsy", "tessdata");

    public static readonly IReadOnlyList<TessdataLang> Catalog = new TessdataLang[]
    {
        new("eng",     "English",                        "~4 MB"),
        new("rus",     "Russian / Русский",               "~4 MB"),
        new("deu",     "German / Deutsch",               "~4 MB"),
        new("fra",     "French / Français",              "~4 MB"),
        new("spa",     "Spanish / Español",              "~4 MB"),
        new("ita",     "Italian / Italiano",             "~3 MB"),
        new("por",     "Portuguese / Português",         "~4 MB"),
        new("pol",     "Polish / Polski",                "~5 MB"),
        new("nld",     "Dutch / Nederlands",             "~3 MB"),
        new("tur",     "Turkish / Türkçe",               "~4 MB"),
        new("ukr",     "Ukrainian / Українська",         "~4 MB"),
        new("chi_sim", "Chinese Simplified / 简体中文",  "~18 MB"),
        new("chi_tra", "Chinese Traditional / 繁體中文", "~22 MB"),
        new("jpn",     "Japanese / 日本語",              "~14 MB"),
        new("kor",     "Korean / 한국어",                "~5 MB"),
        new("ara",     "Arabic / العربية",               "~6 MB"),
    };

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
