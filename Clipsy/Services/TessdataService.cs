using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <param name="GitBlobSha1">Git object id of the file at <see cref="TessdataService.Commit"/>; verifies the download.</param>
public sealed record TessdataLang(string Code, string DisplayName, long Bytes, string GitBlobSha1)
{
    public string ApproxSize => $"~{Math.Max(1, (int)Math.Round(Bytes / 1048576.0))} MB";
}

public static class TessdataService
{
    // tessdata_best (accurate LSTM models), pinned to one commit so the bytes can't change under us.
    public const string Commit = "e12c65a915945e4c28e237a9b52bc4a8f39a0cec";
    private const string BaseUrl = "https://raw.githubusercontent.com/tesseract-ocr/tessdata_best/" + Commit + "/";

    private static readonly HttpClient Http = CreateClient();

    public static readonly string StorageDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clipsy", "tessdata");

    public static readonly IReadOnlyList<TessdataLang> Catalog = new TessdataLang[]
    {
        new("eng",     "English",                        15400601, "176dc3220de7db34d3b3aecbfa42043a6038348b"),
        new("rus",     "Russian / Русский",               15301764, "702e3c43c863cf3325002c9d2cc38045e01314a9"),
        new("deu",     "German / Deutsch",                8628461, "6dd654880db617f3edfa00bc2ed9c9e3107b8fd0"),
        new("fra",     "French / Français",               3972885, "f73e7dec0669dbac6443d8f83054b8b2a3146729"),
        new("spa",     "Spanish / Español",              13570187, "18ceb5679049d25f9e5486a1571359829530e7e0"),
        new("ita",     "Italian / Italiano",              8863635, "8a09dcab553d22307b8110af0848ed71e5560cc8"),
        new("por",     "Portuguese / Português",          8159939, "01743fdba931e706f79d86620ce18424036220f2"),
        new("pol",     "Polish / Polski",                11978867, "6c149bd1f9bc6da3948ebc81be1a0d9de0a597f4"),
        new("nld",     "Dutch / Nederlands",              8903736, "e4d30d0e558bab61d618b60722764a1a30596a04"),
        new("tur",     "Turkish / Türkçe",                7456265, "297ecddbccd5e597037d2be34043bb0a0c53f3be"),
        new("ukr",     "Ukrainian / Українська",         10859081, "be88f63252a3d9e81cdac161e11e4e8498129cfa"),
        new("chi_sim", "Chinese Simplified / 简体中文",   13077423, "da7fa49ded2895ebe974c6ea3a6cc1af8595f1af"),
        new("chi_tra", "Chinese Traditional / 繁體中文",  12985735, "c1dd109cd5bbec27381f578e9a425cef88466244"),
        new("jpn",     "Japanese / 日本語",               14330109, "d2a8eab80ba3aba7654ac789b82f70b64d1cf051"),
        new("kor",     "Korean / 한국어",                 12528128, "c82615b5228c9981c33a0985b32224d97b7fb43a"),
        new("ara",     "Arabic / العربية",                12603724, "4b687c7d54c86cf6219aebe781b47284fd5d41f0"),
    };

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Clipsy");
        return c;
    }

    public static (int Min, int Max) ApproxSizeRangeMb()
    {
        var mb = Catalog.Select(c => Math.Max(1, (int)Math.Round(c.Bytes / 1048576.0))).ToList();
        return mb.Count == 0 ? (0, 0) : (mb.Min(), mb.Max());
    }

    private static string PathFor(string code) => Path.Combine(StorageDir, code + ".traineddata");

    public static bool IsInstalled(string code) => File.Exists(PathFor(code));

    public static IReadOnlyList<string> InstalledSelectedCodes()
    {
        return SettingsService.Instance.Settings.TesseractLanguages
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsInstalled)
            .ToList();
    }

    public static async Task DownloadAsync(string code, IProgress<int> progress, CancellationToken ct = default)
    {
        var lang = Catalog.FirstOrDefault(c => c.Code == code)
            ?? throw new ArgumentOutOfRangeException(nameof(code));
        Directory.CreateDirectory(StorageDir);
        var tmp = PathFor(code) + ".tmp";
        try
        {
            using (var response = await Http.GetAsync(BaseUrl + code + ".traineddata", HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? lang.Bytes;
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
                var buffer = new byte[65536];
                long downloaded = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0) progress?.Report((int)Math.Min(99, downloaded * 100 / total));
                }
            }

            var sha = await GitBlobSha1Async(tmp, ct);
            if (!string.Equals(sha, lang.GitBlobSha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"tessdata '{code}' failed verification (got {sha}).");

            TesseractOcrEngine.Reset();
            File.Move(tmp, PathFor(code), overwrite: true);
            progress?.Report(100);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Returns false when the file is locked or can't be removed.</summary>
    public static bool Delete(string code)
    {
        try
        {
            TesseractOcrEngine.Reset();
            var path = PathFor(code);
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"tessdata delete '{code}' failed", ex);
            return false;
        }
    }

    // Git object id: SHA-1 over "blob <size>\0" followed by the content.
    internal static async Task<string> GitBlobSha1Async(string path, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        sha.AppendData(Encoding.ASCII.GetBytes($"blob {fs.Length}\0"));
        var buffer = new byte[65536];
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
            sha.AppendData(buffer, 0, read);
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }
}
