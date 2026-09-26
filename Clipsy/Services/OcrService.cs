using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using RapidOCRSharpOnnx.Configurations;
using RapidOCRSharpOnnx.Inference;
using RapidOCRSharpOnnx.Providers;
using RapidOCRSharpOnnx.Utils;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Clipsy.Services;

public sealed record OcrWord(string Text, Rect BoundsPixels);

public interface IOcrEngine
{
    Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] pngBytes);
}

/// <summary>Default OCR engine (Windows.Media.Ocr): local, languages via FoD,
/// returns word boxes in the bitmap's pixel space.</summary>
public sealed class WinRtOcrEngine : IOcrEngine
{
    public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] pngBytes)
    {
        var ras = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(pngBytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        ras.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(ras);
        using var soft = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null)
        {
            return Array.Empty<OcrWord>();
        }
        var result = await engine.RecognizeAsync(soft);
        var words = new List<OcrWord>();
        foreach (var line in result.Lines)
        {
            foreach (var w in line.Words)
            {
                words.Add(new OcrWord(w.Text, w.BoundingRect));
            }
        }
        return words;
    }
}

/// <summary>Tesseract OCR engine (user-downloaded tessdata via TessdataService);
/// falls back silently if no language files are installed.</summary>
public sealed class TesseractOcrEngine : IOcrEngine
{
    public Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] pngBytes)
    {
        return Task.Run<IReadOnlyList<OcrWord>>(() =>
        {
            try
            {
                var langs = TessdataService.InstalledSelectedCodes();
                if (langs.Count == 0)
                    throw new InvalidOperationException("No Tesseract language files installed.");

                // Grayscale + auto-invert dark-theme captures: Tesseract expects
                // dark text on a light background; light-on-dark garbles badly.
                var prepped = Preprocess(pngBytes);
                using var srcPix = Tesseract.Pix.LoadFromMemory(prepped);

                // Upscale so the longest side reaches ~2400px (Tesseract likes
                // ~300dpi); bounds come back scaled, so divide them back after.
                float scaleUp = 1f;
                int longest = Math.Max(srcPix.Width, srcPix.Height);
                if (longest > 0 && longest < 2400)
                    scaleUp = Math.Min(3f, 2400f / longest);

                bool scaled = scaleUp > 1.01f;
                Tesseract.Pix pix = scaled ? srcPix.Scale(scaleUp, scaleUp) : srcPix;
                try
                {
                    // Best mean-confidence single language: a combined eng+rus pass
                    // transliterates Cyrillic lookalikes to Latin (ракета -> paketa).
                    List<OcrWord>? best = null;
                    float bestConf = -1f;
                    foreach (var lang in langs)
                    {
                        var (w, conf) = RunSingle(lang, pix, scaleUp);
                        if (conf > bestConf) { bestConf = conf; best = w; }
                    }
                    return best ?? new List<OcrWord>();
                }
                finally
                {
                    if (scaled) pix.Dispose();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Clipsy] Tesseract failed: {ex.Message}");
                return new List<OcrWord>();
            }
        });
    }

    // Grayscale, then invert if the image is mostly dark (light-on-dark UI), so
    // Tesseract gets dark text on a light background. Geometry is unchanged.
    private static byte[] Preprocess(byte[] png)
    {
        try
        {
            using var ms = new MemoryStream(png);
            using var src = new Bitmap(ms);
            int w = src.Width, h = src.Height;
            using var gray = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(gray))
            {
                var cm = new ColorMatrix(new[]
                {
                    new[] { 0.299f, 0.299f, 0.299f, 0f, 0f },
                    new[] { 0.587f, 0.587f, 0.587f, 0f, 0f },
                    new[] { 0.114f, 0.114f, 0.114f, 0f, 0f },
                    new[] { 0f, 0f, 0f, 1f, 0f },
                    new[] { 0f, 0f, 0f, 0f, 1f },
                });
                using var ia = new ImageAttributes();
                ia.SetColorMatrix(cm);
                g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, w, h, GraphicsUnit.Pixel, ia);
            }

            var data = gray.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                int bytes = Math.Abs(data.Stride) * h;
                var buf = new byte[bytes];
                Marshal.Copy(data.Scan0, buf, 0, bytes);

                long sum = 0;
                for (int i = 0; i < bytes; i += 3) sum += buf[i];
                long count = bytes / 3;
                double mean = count > 0 ? (double)sum / count : 255;

                if (mean < 110) // dark background → invert
                {
                    for (int i = 0; i < bytes; i++) buf[i] = (byte)(255 - buf[i]);
                    Marshal.Copy(buf, 0, data.Scan0, bytes);
                }
            }
            finally { gray.UnlockBits(data); }

            using var outMs = new MemoryStream();
            gray.Save(outMs, ImageFormat.Png);
            return outMs.ToArray();
        }
        catch
        {
            return png; // preprocessing is best-effort
        }
    }

    private static (List<OcrWord> words, float confidence) RunSingle(string lang, Tesseract.Pix pix, float scaleUp)
    {
        var words = new List<OcrWord>();
        try
        {
            using var engine = new Tesseract.TesseractEngine(TessdataService.StorageDir, lang, Tesseract.EngineMode.Default);
            using var page = engine.Process(pix);
            float conf = page.GetMeanConfidence();

            double inv = 1.0 / scaleUp;
            using var iter = page.GetIterator();
            iter.Begin();
            do
            {
                if (iter.TryGetBoundingBox(Tesseract.PageIteratorLevel.Word, out var r))
                {
                    var text = iter.GetText(Tesseract.PageIteratorLevel.Word)?.Trim();
                    if (!string.IsNullOrEmpty(text))
                        words.Add(new OcrWord(text,
                            new Rect(r.X1 * inv, r.Y1 * inv, (r.X2 - r.X1) * inv, (r.Y2 - r.Y1) * inv)));
                }
            }
            while (iter.Next(Tesseract.PageIteratorLevel.Word));
            return (words, conf);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Tesseract lang '{lang}' failed: {ex.Message}");
            return (words, -1f);
        }
    }
}

public sealed class PpOcrV5Engine : IOcrEngine
{
    private const float MinimumTextScore = 0.45f;
    private const float LineCorrectionMargin = 0.08f;
    private static readonly object Sync = new();
    private static IOcrDetector? _detector;
    private static List<(PpOcrV5RecognizerSpec Spec, IOcrRecognizer Recognizer)>? _recognizers;

    private sealed record LineInfo(int[] ItemIndices, OpenCvSharp.Rect CropRect);

    public static void Reset()
    {
        lock (Sync)
        {
            try { _detector?.Dispose(); } catch { }
            _detector = null;
            if (_recognizers != null)
            {
                foreach (var (_, recognizer) in _recognizers)
                    try { recognizer.Dispose(); } catch { }
            }
            _recognizers = null;
        }
    }

    public Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] pngBytes)
    {
        return Task.Run<IReadOnlyList<OcrWord>>(() =>
        {
            if (!PpOcrV5Service.IsReady)
                return Array.Empty<OcrWord>();

            lock (Sync)
            {
                try
                {
                    EnsureInitialized();
                    using var image = OpenCvSharp.Cv2.ImDecode(
                        pngBytes, OpenCvSharp.ImreadModes.Color);
                    if (image.Empty())
                        return Array.Empty<OcrWord>();

                    var detected = _detector!.TextDetect(image).Data;
                    if (detected?.ImgCropList == null ||
                        detected.ImgCropList.Count == 0 ||
                        detected.DetItems == null ||
                        detected.DetItems.Length == 0)
                        return Array.Empty<OcrWord>();

                    using (detected.ImgCropList)
                    {
                        var lines = BuildLineInfos(detected, image);
                        if (lines.Count == 0)
                            return Array.Empty<OcrWord>();

                        using var lineCrops = BuildLineCrops(image, lines);
                        int lineCount = lines.Count;
                        var bestModel = Enumerable.Repeat(-1, lineCount).ToArray();
                        var bestLineText = Enumerable.Repeat(string.Empty, lineCount).ToArray();
                        var bestLineScore = new float[lineCount];
                        var bestRank = Enumerable.Repeat(float.NegativeInfinity, lineCount).ToArray();

                        for (int modelIndex = 0; modelIndex < _recognizers!.Count; modelIndex++)
                        {
                            var (spec, recognizer) = _recognizers[modelIndex];
                            var recognized = recognizer.TextRecognize(lineCrops).Data;
                            int count = Math.Min(lineCount, recognized.Length);

                            for (int lineIndex = 0; lineIndex < count; lineIndex++)
                            {
                                string text = Compact(recognized[lineIndex].Label);
                                if (text.Length == 0)
                                    continue;

                                float rank = RankCandidate(spec.Key, text, recognized[lineIndex].Score);
                                if (rank <= bestRank[lineIndex])
                                    continue;

                                bestRank[lineIndex] = rank;
                                bestModel[lineIndex] = modelIndex;
                                bestLineText[lineIndex] = text;
                                bestLineScore[lineIndex] = recognized[lineIndex].Score;
                            }
                        }

                        var wordResultsByModel = new Dictionary<int, RapidOCRSharpOnnx.Models.RecResult[]>();
                        foreach (int modelIndex in bestModel.Where(i => i >= 0).Distinct())
                        {
                            var recognized = _recognizers[modelIndex].Recognizer
                                .TextRecognize(detected.ImgCropList).Data;
                            wordResultsByModel[modelIndex] = recognized;
                        }

                        var words = new List<OcrWord>(detected.DetItems.Length);
                        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                        {
                            int modelIndex = bestModel[lineIndex];
                            if (modelIndex < 0 ||
                                !wordResultsByModel.TryGetValue(modelIndex, out var wordResults))
                                continue;

                            var line = lines[lineIndex];
                            var rawTexts = new string[line.ItemIndices.Length];
                            var rawScores = new float[line.ItemIndices.Length];

                            for (int i = 0; i < line.ItemIndices.Length; i++)
                            {
                                int itemIndex = line.ItemIndices[i];
                                if (itemIndex >= wordResults.Length)
                                    continue;

                                rawTexts[i] = wordResults[itemIndex].Label?.Trim() ?? string.Empty;
                                rawScores[i] = wordResults[itemIndex].Score;
                            }

                            var segments = SegmentLineText(bestLineText[lineIndex], rawTexts);
                            for (int i = 0; i < line.ItemIndices.Length; i++)
                            {
                                int itemIndex = line.ItemIndices[i];
                                if (itemIndex >= detected.DetItems.Length)
                                    continue;

                                string raw = rawTexts[i] ?? string.Empty;
                                string segment = i < segments.Length ? segments[i] : string.Empty;
                                string text = ShouldUseLineSegment(
                                    raw,
                                    rawScores[i],
                                    segment,
                                    bestLineScore[lineIndex])
                                    ? segment
                                    : raw;

                                if (string.IsNullOrWhiteSpace(text))
                                    continue;

                                var bounds = MapBoxToOriginal(detected, detected.DetItems[itemIndex].Box);
                                if (bounds.Width <= 0 || bounds.Height <= 0)
                                    continue;

                                float score = rawScores[i] > 0
                                    ? Math.Max(rawScores[i], bestLineScore[lineIndex])
                                    : bestLineScore[lineIndex];
                                if (score < MinimumTextScore)
                                    continue;

                                words.Add(new OcrWord(text, bounds));
                            }
                        }

                        return words;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Clipsy] PP-OCRv5 failed: {ex.Message}");
                    Reset();
                    return Array.Empty<OcrWord>();
                }
            }
        });
    }

    private static void EnsureInitialized()
    {
        if (_detector != null && _recognizers != null)
            return;

        var created = new List<(PpOcrV5RecognizerSpec, IOcrRecognizer)>();
        IOcrDetector? detector = null;
        try
        {
            var installedModels = PpOcrV5Service.InstalledRecognizerModels();
            if (installedModels.Count == 0)
                throw new InvalidOperationException("No PP-OCRv5 recognizer models installed.");

            var first = installedModels[0];
            detector = new ExecutionProviderCPU(CreateConfig(first)).CreateDetector();

            foreach (var spec in installedModels)
            {
                var recognizer = new ExecutionProviderCPU(CreateConfig(spec)).CreateRecognizer();
                created.Add((spec, recognizer));
            }

            _detector = detector;
            _recognizers = created;
        }
        catch
        {
            try { detector?.Dispose(); } catch { }
            foreach (var (_, recognizer) in created)
                try { recognizer.Dispose(); } catch { }
            throw;
        }
    }

    private static OcrConfig CreateConfig(PpOcrV5RecognizerSpec spec) =>
        new(PpOcrV5Service.DetectorPath, spec.Path, spec.Language, OCRVersion.PPOCRV5)
        {
            MaxSideLen = 2400,
        };

    private static List<LineInfo> BuildLineInfos(
        RapidOCRSharpOnnx.Inference.PPOCR_Det.DetResult detected,
        OpenCvSharp.Mat image)
    {
        var lines = new List<LineInfo>();

        foreach (var group in detected.DetItems
                     .Select((item, index) => (item, index))
                     .GroupBy(x => x.item.LineId)
                     .OrderBy(g => g.Key))
        {
            var indices = group.Select(x => x.index).ToArray();
            if (indices.Length == 0)
                continue;

            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;

            foreach (int index in indices)
            {
                var rect = MapBoxToOriginal(detected, detected.DetItems[index].Box);
                minX = Math.Min(minX, rect.X);
                minY = Math.Min(minY, rect.Y);
                maxX = Math.Max(maxX, rect.X + rect.Width);
                maxY = Math.Max(maxY, rect.Y + rect.Height);
            }

            int x0 = Math.Max(0, (int)Math.Floor(minX) - 3);
            int y0 = Math.Max(0, (int)Math.Floor(minY) - 3);
            int x1 = Math.Min(image.Width, (int)Math.Ceiling(maxX) + 3);
            int y1 = Math.Min(image.Height, (int)Math.Ceiling(maxY) + 3);
            if (x1 <= x0 || y1 <= y0)
                continue;

            lines.Add(new LineInfo(
                indices,
                new OpenCvSharp.Rect(x0, y0, x1 - x0, y1 - y0)));
        }

        return lines;
    }

    private static DisposableList<RapidOCRSharpOnnx.Inference.PPOCR_Rec.Models.ImageIndex>
        BuildLineCrops(OpenCvSharp.Mat image, IReadOnlyList<LineInfo> lines)
    {
        var crops =
            new DisposableList<RapidOCRSharpOnnx.Inference.PPOCR_Rec.Models.ImageIndex>();
        for (int i = 0; i < lines.Count; i++)
        {
            var crop = new OpenCvSharp.Mat(image, lines[i].CropRect).Clone();
            crops.Add(new RapidOCRSharpOnnx.Inference.PPOCR_Rec.Models.ImageIndex(crop, i));
        }
        return crops;
    }

    private static Rect MapBoxToOriginal(
        RapidOCRSharpOnnx.Inference.PPOCR_Det.DetResult detected,
        OpenCvSharp.Point2f[]? box)
    {
        if (box == null || box.Length == 0)
            return new Rect();

        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity;
        double maxY = double.NegativeInfinity;

        foreach (var p in box)
        {
            double x = Math.Clamp(
                (p.X - detected.ResizeData.PaddingLeft) * detected.ResizeData.RatioW,
                0, detected.OriginalWidth);
            double y = Math.Clamp(
                (p.Y - detected.ResizeData.PaddingTop) * detected.ResizeData.RatioH,
                0, detected.OriginalHeight);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        return maxX > minX && maxY > minY
            ? new Rect(minX, minY, maxX - minX, maxY - minY)
            : new Rect();
    }

    private static string[] SegmentLineText(string lineText, IReadOnlyList<string> rawWords)
    {
        var result = new string[rawWords.Count];
        string compact = Compact(lineText);
        if (compact.Length == 0 || rawWords.Count == 0)
            return result;

        if (rawWords.Count == 1)
        {
            result[0] = compact;
            return result;
        }

        var weights = rawWords
            .Select(w => Math.Max(1, Compact(w).Length))
            .ToArray();
        int totalWeight = weights.Sum();

        int offset = 0;
        int cumulativeWeight = 0;
        for (int i = 0; i < rawWords.Count; i++)
        {
            if (i == rawWords.Count - 1)
            {
                result[i] = compact[offset..];
                break;
            }

            cumulativeWeight += weights[i];
            int target = (int)Math.Round(
                compact.Length * (double)cumulativeWeight / totalWeight);

            int minEnd = Math.Min(compact.Length, offset + 1);
            int remainingWords = rawWords.Count - i - 1;
            int maxEnd = Math.Max(
                minEnd,
                compact.Length - remainingWords);
            int end = Math.Clamp(target, minEnd, maxEnd);

            result[i] = compact[offset..end];
            offset = end;
        }

        return result;
    }

    private static bool ShouldUseLineSegment(
        string raw,
        float rawScore,
        string segment,
        float lineScore)
    {
        raw = Compact(raw);
        segment = Compact(segment);

        if (segment.Length == 0)
            return false;
        if (raw.Length == 0)
            return true;
        if (string.Equals(raw, segment, StringComparison.Ordinal))
            return false;

        return lineScore >= rawScore + LineCorrectionMargin ||
               (rawScore < 0.75f && lineScore >= 0.85f);
    }

    private static string Compact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var chars = text.Where(ch => !char.IsWhiteSpace(ch)).ToArray();
        return new string(chars);
    }

    private static float RankCandidate(string modelKey, string text, float score)
    {
        if (score <= 0)
            return score;

        string script = DetectDominantScript(text);
        float bonus = 0;

        if (script == "ascii")
        {
            if (modelKey == "en") bonus = 0.06f;
            else if (modelKey == "latin") bonus = 0.03f;
        }
        else if ((script == "cjk" && modelKey == "ch") ||
                 (script == "latin" && modelKey == "latin") ||
                 (script == "cyrillic" &&
                    (modelKey == "cyrillic" || modelKey == "eslav")) ||
                 (script == "korean" && modelKey == "korean") ||
                 (script == "th" && modelKey == "th") ||
                 (script == "el" && modelKey == "el") ||
                 (script == "arabic" && modelKey == "arabic") ||
                 (script == "devanagari" && modelKey == "devanagari") ||
                 (script == "ta" && modelKey == "ta") ||
                 (script == "te" && modelKey == "te"))
        {
            bonus = 0.08f;
        }

        return score + bonus;
    }

    private static string DetectDominantScript(string text)
    {
        int ascii = 0, latin = 0, cyrillic = 0, cjk = 0, korean = 0;
        int th = 0, el = 0, arabic = 0, devanagari = 0, ta = 0, te = 0;

        foreach (char ch in text)
        {
            if (!char.IsLetter(ch))
                continue;

            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z')) ascii++;
            else if ((ch >= '\u00C0' && ch <= '\u024F') ||
                     (ch >= '\u1E00' && ch <= '\u1EFF')) latin++;
            else if (ch >= '\u0400' && ch <= '\u052F') cyrillic++;
            else if ((ch >= '\u3040' && ch <= '\u30FF') ||
                     (ch >= '\u3400' && ch <= '\u9FFF')) cjk++;
            else if ((ch >= '\u1100' && ch <= '\u11FF') ||
                     (ch >= '\uAC00' && ch <= '\uD7AF')) korean++;
            else if (ch >= '\u0E00' && ch <= '\u0E7F') th++;
            else if (ch >= '\u0370' && ch <= '\u03FF') el++;
            else if ((ch >= '\u0600' && ch <= '\u06FF') ||
                     (ch >= '\u0750' && ch <= '\u077F') ||
                     (ch >= '\u08A0' && ch <= '\u08FF')) arabic++;
            else if (ch >= '\u0900' && ch <= '\u097F') devanagari++;
            else if (ch >= '\u0B80' && ch <= '\u0BFF') ta++;
            else if (ch >= '\u0C00' && ch <= '\u0C7F') te++;
        }

        var scripts = new (string Name, int Count)[]
        {
            ("latin", latin),
            ("cyrillic", cyrillic),
            ("cjk", cjk),
            ("korean", korean),
            ("th", th),
            ("el", el),
            ("arabic", arabic),
            ("devanagari", devanagari),
            ("ta", ta),
            ("te", te),
        };

        var best = scripts.OrderByDescending(x => x.Count).First();
        if (best.Count > 0)
            return best.Name;

        return ascii > 0 ? "ascii" : "neutral";
    }
}

/// <summary>Best-effort language hint when OCR returned nothing: runs Windows OCR
/// as a script detector and maps a dominant Unicode range to a Tesseract code.</summary>
public static class OcrLanguageHint
{
    public static async Task<TessdataLang?> DetectAsync(byte[] pngBytes)
    {
        try
        {
            var words = await new WinRtOcrEngine().RecognizeAsync(pngBytes);
            if (words.Count == 0) return null;

            int latin = 0, cyrillic = 0, cjk = 0, kana = 0, hangul = 0, arabic = 0, total = 0;
            foreach (var w in words)
            {
                foreach (var ch in w.Text)
                {
                    if (char.IsWhiteSpace(ch) || char.IsDigit(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch))
                        continue;
                    total++;
                    if (ch >= 0x4E00 && ch <= 0x9FFF) cjk++;
                    else if (ch >= 0x3040 && ch <= 0x30FF) kana++;
                    else if (ch >= 0xAC00 && ch <= 0xD7A3) hangul++;
                    else if (ch >= 0x0600 && ch <= 0x06FF) arabic++;
                    else if (ch >= 0x0400 && ch <= 0x04FF) cyrillic++;
                    else if (ch < 0x0250) latin++;
                }
            }
            if (total < 3) return null;

            // Kana is unambiguous Japanese even when kanji outnumber it.
            string? best;
            if (kana >= 2) best = "jpn";
            else
            {
                // Pick the dominant script; require a clear majority for confidence.
                (string code, int count)[] scripts =
                {
                    ("kor", hangul),
                    ("ara", arabic),
                    ("chi_sim", cjk),
                    ("rus", cyrillic),
                    ("eng", latin),
                };
                best = null; int bestCount = 0;
                foreach (var (code, count) in scripts)
                    if (count > bestCount) { bestCount = count; best = code; }
                if (best == null || bestCount < total * 0.4) return null;
            }
            return TessdataService.Catalog.FirstOrDefault(c => c.Code == best);
        }
        catch
        {
            return null;
        }
    }
}

public static class OcrEngineFactory
{
    public static IOcrEngine Resolve()
    {
        var configured = SettingsService.Instance.Settings.OcrEngine;
        if (string.Equals(configured, "Tesseract", StringComparison.OrdinalIgnoreCase)
            && TessdataService.InstalledSelectedCodes().Count > 0)
            return new TesseractOcrEngine();
        if (string.Equals(configured, "PPOCRv5", StringComparison.OrdinalIgnoreCase)
            && PpOcrV5Service.IsReady)
            return new PpOcrV5Engine();
        return new WinRtOcrEngine();
    }
}
