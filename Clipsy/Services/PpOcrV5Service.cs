using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using RapidOCRSharpOnnx.Utils;

namespace Clipsy.Services;

public sealed record PpOcrV5RecognizerSpec(
    string Key,
    string DisplayName,
    string ApproxSize,
    string FileName,
    string Url,
    string Sha256,
    LangRec Language)
{
    public string Path => System.IO.Path.Combine(PpOcrV5Service.StorageDir, FileName);
}

public static class PpOcrV5Service
{
    private static readonly SemaphoreSlim DownloadConcurrencyGate = new(3, 3);
    private static readonly SemaphoreSlim DetectorDownloadGate = new(1, 1);

    private const string BaseUrl =
        "https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.9.2/onnx/PP-OCRv5";
    private const string DetectorSha256 =
        "4d97c44a20d30a81aad087d6a396b08f786c4635742afc391f6621f5c6ae78ae";

    public static readonly PpOcrV5RecognizerSpec[] RecognizerModels =
    [
        new("ch", "Chinese / Japanese", "15.9 MB", "ch.onnx",
            $"{BaseUrl}/rec/ch_PP-OCRv5_rec_mobile.onnx",
            "5825fc7ebf84ae7a412be049820b4d86d77620f204a041697b0494669b1742c5", LangRec.CH),
        new("en", "English", "7.5 MB", "en.onnx",
            $"{BaseUrl}/rec/en_PP-OCRv5_rec_mobile.onnx",
            "c3461add59bb4323ecba96a492ab75e06dda42467c9e3d0c18db5d1d21924be8", LangRec.EN),
        new("latin", "Latin", "7.5 MB", "latin.onnx",
            $"{BaseUrl}/rec/latin_PP-OCRv5_rec_mobile.onnx",
            "b20bd37c168a570f583afbc8cd7925603890efbcdc000a59e22c269d160b5f5a", LangRec.LATIN),
        new("cyrillic", "Cyrillic", "7.7 MB", "cyrillic.onnx",
            $"{BaseUrl}/rec/cyrillic_PP-OCRv5_rec_mobile.onnx",
            "90f761b4bfcce0c8c561c0cb5c887b0971d3ec01c32164bdf7374a35b0982711", LangRec.CYRILLIC),
        new("eslav", "East Slavic", "7.5 MB", "eslav.onnx",
            $"{BaseUrl}/rec/eslav_PP-OCRv5_rec_mobile.onnx",
            "08705d6721849b1347d26187f15a5e362c431963a2a62bfff4feac578c489aab", LangRec.ESLAV),
        new("korean", "Korean", "12.9 MB", "korean.onnx",
            $"{BaseUrl}/rec/korean_PP-OCRv5_rec_mobile.onnx",
            "cd6e2ea50f6943ca7271eb8c56a877a5a90720b7047fe9c41a2e541a25773c9b", LangRec.KOREAN),
        new("th", "Thai", "7.5 MB", "th.onnx",
            $"{BaseUrl}/rec/th_PP-OCRv5_rec_mobile.onnx",
            "de541dd83161c241ff426f7ecfd602a0ba77d686cf3ab9a6c255ea82fd08006e", LangRec.TH),
        new("el", "Greek", "7.5 MB", "el.onnx",
            $"{BaseUrl}/rec/el_PP-OCRv5_rec_mobile.onnx",
            "b4368bccd557123c702b7549fee6cd1e94b581337d1c9b65310f109131542b7f", LangRec.EL),
        new("arabic", "Arabic", "7.7 MB", "arabic.onnx",
            $"{BaseUrl}/rec/arabic_PP-OCRv5_rec_mobile.onnx",
            "c1192e632d0baa9146ae5b756a0e635e3dc63c1733737ebfd1629e87144e9295", LangRec.ARABIC),
        new("devanagari", "Devanagari", "7.6 MB", "devanagari.onnx",
            $"{BaseUrl}/rec/devanagari_PP-OCRv5_rec_mobile.onnx",
            "d6f0a906580e3fa6b324a318718f1f31f268b6ea8ef985f91c2012a37f52c91e", LangRec.DEVANAGARI),
        new("ta", "Tamil", "7.5 MB", "ta.onnx",
            $"{BaseUrl}/rec/ta_PP-OCRv5_rec_mobile.onnx",
            "a42448808b7dea87597336f12438935f40353f1949e8360acd9e06b4da21bfe1", LangRec.TA),
        new("te", "Telugu", "7.6 MB", "te.onnx",
            $"{BaseUrl}/rec/te_PP-OCRv5_rec_mobile.onnx",
            "a3690451b50028a09a3316a1274f7c05728151ea3f8fd392696397a7fefcbd92", LangRec.TE),
    ];

    public static readonly string StorageDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Clipsy", "ppocrv5");

    public static string DetectorPath => System.IO.Path.Combine(StorageDir, "det.onnx");

    public static IReadOnlyList<PpOcrV5RecognizerSpec> InstalledRecognizerModels() =>
        RecognizerModels.Where(m => File.Exists(m.Path)).ToArray();

    public static bool IsModelInstalled(string key) =>
        RecognizerModels.FirstOrDefault(m =>
            string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)) is { } model &&
        File.Exists(model.Path);

    public static bool IsReady =>
        File.Exists(DetectorPath) && RecognizerModels.Any(m => File.Exists(m.Path));

    public static async Task DownloadModelAsync(
        string key,
        IProgress<int>? progress,
        CancellationToken ct = default)
    {
        var model = RecognizerModels.FirstOrDefault(m =>
            string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentOutOfRangeException(nameof(key));

        await DownloadConcurrencyGate.WaitAsync(ct);
        try
        {
            PpOcrV5Engine.Reset();
            Directory.CreateDirectory(StorageDir);

            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Clipsy/1.0");

            bool detectorWasMissing = !File.Exists(DetectorPath);
            if (detectorWasMissing)
            {
                await EnsureDetectorAsync(client, progress, ct);
                progress?.Report(35);
            }

            await DownloadAndVerifyAsync(
                client,
                model.Url,
                model.Path,
                model.Sha256,
                detectorWasMissing ? 35 : 0,
                detectorWasMissing ? 65 : 100,
                progress,
                ct);

            progress?.Report(100);
        }
        finally
        {
            DownloadConcurrencyGate.Release();
        }
    }

    public static void DeleteModel(string key)
    {
        var model = RecognizerModels.FirstOrDefault(m =>
            string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
        if (model == null)
            return;

        PpOcrV5Engine.Reset();
        TryDelete(model.Path);
        TryDelete(model.Path + ".tmp");

        if (!RecognizerModels.Any(m => File.Exists(m.Path)))
        {
            TryDelete(DetectorPath);
            TryDelete(DetectorPath + ".tmp");
        }

        CleanupEmptyDirectory();
    }

    private static async Task EnsureDetectorAsync(
        HttpClient client,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        await DetectorDownloadGate.WaitAsync(ct);
        try
        {
            if (File.Exists(DetectorPath))
                return;

            await DownloadAndVerifyAsync(
                client,
                $"{BaseUrl}/det/ch_PP-OCRv5_det_mobile.onnx",
                DetectorPath,
                DetectorSha256,
                0,
                35,
                progress,
                ct);
        }
        finally
        {
            DetectorDownloadGate.Release();
        }
    }

    private static async Task DownloadAndVerifyAsync(
        HttpClient client,
        string url,
        string path,
        string sha256,
        int basePercent,
        int spanPercent,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        var tmp = path + ".tmp";
        TryDelete(tmp);

        try
        {
            using var response = await client.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? -1;

            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(
                tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(
                           buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0)
                    {
                        double fileProgress = Math.Clamp((double)done / total, 0, 1);
                        progress?.Report(basePercent +
                            (int)Math.Round(fileProgress * spanPercent));
                    }
                }

                await output.FlushAsync(ct);
            }

            await VerifySha256Async(tmp, sha256, ct);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static async Task VerifySha256Async(
        string path,
        string expectedHex,
        CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(actual, expectedHex, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"PP-OCRv5 model checksum mismatch: {System.IO.Path.GetFileName(path)}");
    }

    private static void CleanupEmptyDirectory()
    {
        try
        {
            if (Directory.Exists(StorageDir) &&
                !Directory.EnumerateFileSystemEntries(StorageDir).Any())
                Directory.Delete(StorageDir);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipsy] PP-OCRv5 directory cleanup failed: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipsy] PP-OCRv5 cleanup failed: {ex.Message}");
        }
    }
}
