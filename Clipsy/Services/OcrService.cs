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
    private static Dictionary<string, (PpOcrV5RecognizerSpec Spec, IOcrRecognizer Recognizer)>? _recognizers;

    private sealed record LineInfo(int[] ItemIndices, OpenCvSharp.Rect CropRect);
    private sealed record ScriptHint(string Script, IReadOnlyList<OcrWord> Words);

    public static void Reset()
    {
        lock (Sync)
        {
            try { _detector?.Dispose(); } catch { }
            _detector = null;
            if (_recognizers != null)
            {
                foreach (var (_, recognizer) in _recognizers.Values)
                    try { recognizer.Dispose(); } catch { }
            }
            _recognizers = null;
        }
    }

    public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] pngBytes)
    {
        if (!PpOcrV5Service.IsReady)
            return Array.Empty<OcrWord>();

        var hint = await DetectScriptHintAsync(pngBytes).ConfigureAwait(false);
        string scriptHint = hint.Script;
        var scaffoldWords = hint.Words;

        return await Task.Run<IReadOnlyList<OcrWord>>(() =>
        {
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
                        var candidateModels = SelectCandidateModels(scriptHint);
                        var bestModel = new string?[lineCount];
                        var bestLineText = Enumerable.Repeat(string.Empty, lineCount).ToArray();
                        var bestLineScore = new float[lineCount];
                        var bestRank = Enumerable.Repeat(float.NegativeInfinity, lineCount).ToArray();

                        foreach (var spec in candidateModels)
                        {
                            var recognizer = GetRecognizer(spec);
                            var recognized = recognizer.TextRecognize(lineCrops).Data;
                            int count = Math.Min(lineCount, recognized.Length);

                            for (int lineIndex = 0; lineIndex < count; lineIndex++)
                            {
                                string text = NormalizeWhitespace(recognized[lineIndex].Label);
                                if (text.Length == 0)
                                    continue;

                                float rank = RankCandidate(spec.Key, text, recognized[lineIndex].Score);
                                if (rank <= bestRank[lineIndex])
                                    continue;

                                bestRank[lineIndex] = rank;
                                bestModel[lineIndex] = spec.Key;
                                bestLineText[lineIndex] = text;
                                bestLineScore[lineIndex] = recognized[lineIndex].Score;
                            }
                        }

                        var fallbackModels = new HashSet<string>(
                            bestModel.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k!),
                            StringComparer.OrdinalIgnoreCase);

                        var wordResultsByModel = new Dictionary<string, RapidOCRSharpOnnx.Models.RecResult[]>(
                            StringComparer.OrdinalIgnoreCase);
                        foreach (string modelKey in fallbackModels)
                        {
                            var spec = candidateModels.First(m =>
                                string.Equals(m.Key, modelKey, StringComparison.OrdinalIgnoreCase));
                            wordResultsByModel[modelKey] = GetRecognizer(spec)
                                .TextRecognize(detected.ImgCropList).Data;
                        }

                        var words = new List<OcrWord>(Math.Max(detected.DetItems.Length, scaffoldWords.Count));
                        for (int lineIndex = 0; lineIndex < lines.Count; lineIndex++)
                        {
                            string? modelKey = bestModel[lineIndex];
                            if (string.IsNullOrWhiteSpace(modelKey))
                                continue;

                            if (!wordResultsByModel.TryGetValue(modelKey, out var wordResults))
                                continue;

                            var line = lines[lineIndex];
                            var rawTexts = new string[line.ItemIndices.Length];
                            var rawScores = new float[line.ItemIndices.Length];

                            for (int i = 0; i < line.ItemIndices.Length; i++)
                            {
                                int itemIndex = line.ItemIndices[i];
                                if (itemIndex >= wordResults.Length)
                                    continue;

                                rawTexts[i] = NormalizeWhitespace(wordResults[itemIndex].Label);
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
                                if (score < MinimumTextScore ||
                                    IsLikelyIconGarbage(text, score, bounds))
                                    continue;

                                words.AddRange(SplitByScaffoldGaps(image, text, bounds, scaffoldWords));
                            }
                        }

                        return RemoveDuplicateWords(words);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[Clipsy] PP-OCRv5 failed: {ex.Message}");
                    Diagnostics.Log("PP-OCRv5 failed", ex);
                    Reset();
                    return Array.Empty<OcrWord>();
                }
            }
        });
    }

    private static async Task<ScriptHint> DetectScriptHintAsync(byte[] pngBytes)
    {
        try
        {
            var words = await new WinRtOcrEngine().RecognizeAsync(pngBytes).ConfigureAwait(false);
            if (words.Count == 0)
                return new ScriptHint("neutral", Array.Empty<OcrWord>());

            string text = string.Join(' ', words.Select(w => w.Text));
            return new ScriptHint(DetectDominantScript(text), words);
        }
        catch
        {
            return new ScriptHint("neutral", Array.Empty<OcrWord>());
        }
    }

    private static IReadOnlyList<PpOcrV5RecognizerSpec> SelectCandidateModels(string scriptHint)
    {
        var installed = PpOcrV5Service.InstalledRecognizerModels();
        if (installed.Count <= 3 || scriptHint == "neutral")
            return installed;

        string[] preferred = scriptHint switch
        {
            "cyrillic" => ["eslav", "cyrillic"],
            "ascii" => ["en", "latin"],
            "latin" => ["latin", "en"],
            "cjk" => ["ch"],
            "korean" => ["korean"],
            "th" => ["th"],
            "el" => ["el"],
            "arabic" => ["arabic"],
            "devanagari" => ["devanagari"],
            "ta" => ["ta"],
            "te" => ["te"],
            _ => [],
        };

        var selected = preferred
            .Select(key => installed.FirstOrDefault(m =>
                string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(m => m != null);

        return selected != null ? [selected] : installed;
    }
    private static void EnsureInitialized()
    {
        if (_detector != null && _recognizers != null)
            return;

        IOcrDetector? detector = null;
        try
        {
            var installedModels = PpOcrV5Service.InstalledRecognizerModels();
            if (installedModels.Count == 0)
                throw new InvalidOperationException("No PP-OCRv5 recognizer models installed.");

            detector = new ExecutionProviderCPU(CreateConfig(installedModels[0])).CreateDetector();
            _detector = detector;
            _recognizers = new Dictionary<string, (PpOcrV5RecognizerSpec, IOcrRecognizer)>(
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            try { detector?.Dispose(); } catch { }
            throw;
        }
    }

    private static IOcrRecognizer GetRecognizer(PpOcrV5RecognizerSpec spec)
    {
        if (_recognizers!.TryGetValue(spec.Key, out var cached))
            return cached.Recognizer;

        var recognizer = new ExecutionProviderCPU(CreateConfig(spec)).CreateRecognizer();
        _recognizers[spec.Key] = (spec, recognizer);
        return recognizer;
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
        string compact = CompactForCompare(lineText);
        if (compact.Length == 0 || rawWords.Count == 0)
            return result;

        if (rawWords.Count == 1)
        {
            result[0] = compact;
            return result;
        }

        var weights = rawWords
            .Select(w => Math.Max(1, CompactForCompare(w).Length))
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
            int target = (int)Math.Round(compact.Length * (double)cumulativeWeight / totalWeight);
            int minEnd = Math.Min(compact.Length, offset + 1);
            int remaining = rawWords.Count - i - 1;
            int maxEnd = Math.Max(minEnd, compact.Length - remaining);
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
        raw = NormalizeWhitespace(raw);
        segment = NormalizeWhitespace(segment);

        if (segment.Length == 0)
            return false;
        if (raw.Length == 0)
            return true;
        if (string.Equals(raw, segment, StringComparison.Ordinal))
            return false;

        if (string.Equals(CompactForCompare(raw), CompactForCompare(segment), StringComparison.Ordinal) &&
            segment.Contains(' ') && !raw.Contains(' '))
            return true;

        return lineScore >= rawScore + LineCorrectionMargin ||
               (rawScore < 0.75f && lineScore >= 0.85f);
    }

    private static string NormalizeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string CompactForCompare(string? text)
    {
        string normalized = NormalizeWhitespace(text);
        if (normalized.Length == 0)
            return string.Empty;
        return new string(normalized.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
    }

    private static IReadOnlyList<OcrWord> SplitByScaffoldGaps(
        OpenCvSharp.Mat image,
        string text,
        Rect bounds,
        IReadOnlyList<OcrWord> scaffoldWords)
    {
        string normalized = NormalizeWhitespace(text);
        if (normalized.Length == 0)
            return Array.Empty<OcrWord>();

        var scaffold = scaffoldWords
            .Where(w =>
            {
                var b = w.BoundsPixels;
                double cx = b.X + b.Width * 0.5;
                double cy = b.Y + b.Height * 0.5;
                return cx >= bounds.X - 3 &&
                       cx <= bounds.X + bounds.Width + 3 &&
                       cy >= bounds.Y - 3 &&
                       cy <= bounds.Y + bounds.Height + 3;
            })
            .OrderBy(w => w.BoundsPixels.X)
            .ToArray();

        if (scaffold.Length < 2)
            return [new OcrWord(normalized, bounds)];

        int x0 = Math.Clamp((int)Math.Floor(bounds.X), 0, image.Width - 1);
        int y0 = Math.Clamp((int)Math.Floor(bounds.Y), 0, image.Height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(bounds.X + bounds.Width), x0 + 1, image.Width);
        int y1 = Math.Clamp((int)Math.Ceiling(bounds.Y + bounds.Height), y0 + 1, image.Height);
        int width = x1 - x0;
        int height = y1 - y0;
        if (width < 4 || height < 4)
            return [new OcrWord(normalized, bounds)];

        try
        {
            using var crop = new OpenCvSharp.Mat(
                image, new OpenCvSharp.Rect(x0, y0, width, height));
            using var gray = new OpenCvSharp.Mat();
            using var binary = new OpenCvSharp.Mat();
            OpenCvSharp.Cv2.CvtColor(crop, gray, OpenCvSharp.ColorConversionCodes.BGR2GRAY);
            OpenCvSharp.Cv2.Threshold(
                gray, binary, 0, 255,
                OpenCvSharp.ThresholdTypes.Binary | OpenCvSharp.ThresholdTypes.Otsu);

            if (OpenCvSharp.Cv2.CountNonZero(binary) > width * height / 2)
                OpenCvSharp.Cv2.BitwiseNot(binary, binary);

            int maxInkInBlank = Math.Max(0, (int)Math.Round(height * 0.03));
            int minBlankRun = Math.Max(4, (int)Math.Round(height * 0.18));
            var splitXs = new List<int>();
            var splitAfterScaffold = new List<int>();

            for (int i = 0; i < scaffold.Length - 1; i++)
            {
                var left = scaffold[i].BoundsPixels;
                var right = scaffold[i + 1].BoundsPixels;

                int gapStart = Math.Clamp(
                    (int)Math.Ceiling(left.X + left.Width) - x0,
                    0, width - 1);
                int gapEnd = Math.Clamp(
                    (int)Math.Floor(right.X) - x0,
                    gapStart, width);

                if (gapEnd - gapStart < minBlankRun)
                    continue;

                int bestStart = -1;
                int bestLen = 0;
                int runStart = -1;

                for (int x = gapStart; x < gapEnd; x++)
                {
                    int ink = 0;
                    for (int y = 0; y < height; y++)
                    {
                        if (binary.At<byte>(y, x) != 0)
                            ink++;
                    }

                    if (ink <= maxInkInBlank)
                    {
                        if (runStart < 0)
                            runStart = x;
                    }
                    else if (runStart >= 0)
                    {
                        int len = x - runStart;
                        if (len > bestLen)
                        {
                            bestLen = len;
                            bestStart = runStart;
                        }
                        runStart = -1;
                    }
                }

                if (runStart >= 0)
                {
                    int len = gapEnd - runStart;
                    if (len > bestLen)
                    {
                        bestLen = len;
                        bestStart = runStart;
                    }
                }

                if (bestLen < minBlankRun || bestStart < 0)
                    continue;

                splitXs.Add(bestStart + bestLen / 2);
                splitAfterScaffold.Add(i);
            }

            if (splitXs.Count == 0)
                return [new OcrWord(normalized, bounds)];

            string compact = CompactForCompare(normalized);
            if (compact.Length <= splitXs.Count)
                return [new OcrWord(normalized, bounds)];

            var groupWeights = new List<int>();
            int groupStart = 0;
            foreach (int splitAfter in splitAfterScaffold)
            {
                int weight = scaffold
                    .Skip(groupStart)
                    .Take(splitAfter - groupStart + 1)
                    .Sum(w => Math.Max(1, CompactForCompare(w.Text).Length));
                groupWeights.Add(Math.Max(1, weight));
                groupStart = splitAfter + 1;
            }
            groupWeights.Add(Math.Max(
                1,
                scaffold.Skip(groupStart)
                    .Sum(w => Math.Max(1, CompactForCompare(w.Text).Length))));

            int totalWeight = groupWeights.Sum();
            int charOffset = 0;
            int cumulativeWeight = 0;
            var edges = new List<int>(splitXs.Count + 2) { 0 };
            edges.AddRange(splitXs);
            edges.Add(width);
            var result = new List<OcrWord>(groupWeights.Count);

            for (int i = 0; i < groupWeights.Count; i++)
            {
                int charEnd;
                if (i == groupWeights.Count - 1)
                {
                    charEnd = compact.Length;
                }
                else
                {
                    cumulativeWeight += groupWeights[i];
                    int target = (int)Math.Round(
                        compact.Length * (double)cumulativeWeight / totalWeight);
                    int minEnd = charOffset + 1;
                    int remaining = groupWeights.Count - i - 1;
                    int maxEnd = compact.Length - remaining;
                    charEnd = Math.Clamp(target, minEnd, maxEnd);
                }

                string part = compact[charOffset..charEnd];
                if (part.Length > 0)
                {
                    int sx0 = edges[i];
                    int sx1 = edges[i + 1];
                    result.Add(new OcrWord(
                        part,
                        new Rect(
                            x0 + sx0,
                            y0,
                            Math.Max(1, sx1 - sx0),
                            height)));
                }

                charOffset = charEnd;
            }

            return result.Count > 0
                ? result
                : [new OcrWord(normalized, bounds)];
        }
        catch
        {
            return [new OcrWord(normalized, bounds)];
        }
    }
    private static IReadOnlyList<OcrWord> RemoveDuplicateWords(IReadOnlyList<OcrWord> words)
    {
        var result = new List<OcrWord>(words.Count);
        foreach (var word in words)
        {
            string text = NormalizeWhitespace(word.Text);
            if (text.Length == 0)
                continue;

            bool duplicate = result.Any(existing =>
                string.Equals(
                    NormalizeWhitespace(existing.Text),
                    text,
                    StringComparison.OrdinalIgnoreCase) &&
                OverlapOverSmaller(existing.BoundsPixels, word.BoundsPixels) >= 0.35);

            if (!duplicate)
                result.Add(word);
        }
        return result;
    }

    private static double OverlapOverSmaller(Rect a, Rect b)
    {
        double x0 = Math.Max(a.X, b.X);
        double y0 = Math.Max(a.Y, b.Y);
        double x1 = Math.Min(a.X + a.Width, b.X + b.Width);
        double y1 = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (x1 <= x0 || y1 <= y0)
            return 0;

        double intersection = (x1 - x0) * (y1 - y0);
        double smaller = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smaller > 0 ? intersection / smaller : 0;
    }

    private static bool IsLikelyIconGarbage(string text, float score, Rect bounds)
    {
        string normalized = NormalizeWhitespace(text);
        int glyphs = normalized.Count(char.IsLetterOrDigit);
        if (glyphs == 0)
            return true;

        double aspect = bounds.Width / Math.Max(1.0, bounds.Height);
        if (glyphs == 1 &&
            aspect is > 0.70 and < 1.40 &&
            (score < 0.90f || Math.Min(bounds.Width, bounds.Height) >= 18))
            return true;
        if (glyphs >= 3 && aspect < glyphs * 0.28)
            return true;

        return false;
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
