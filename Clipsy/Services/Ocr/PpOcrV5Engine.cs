using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
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

public sealed class PpOcrV5Engine : IOcrEngine
{
    private const float MinimumTextScore = 0.50f;
    private const float RecoveryTextScore = 0.65f;

    private static readonly object Sync = new();
    private static readonly HashSet<string> EmptyScripts =
        new(StringComparer.Ordinal);
    private static IOcrDetector? _detector;
    private static Dictionary<string, (PpOcrV5RecognizerSpec Spec, IOcrRecognizer Recognizer)>? _recognizers;

    private sealed record HintLine(int[] WordIndices, Rect Bounds);
    private sealed record RecoveryRegion(
        Rect Bounds,
        IReadOnlyList<PpOcrV5RecognizerSpec> Models,
        bool PrefixRepairOnly = false);

    public static void Reset()
    {
        lock (Sync)
        {
            try { _detector?.Dispose(); } catch { }
            _detector = null;

            if (_recognizers != null)
            {
                foreach (var (_, recognizer) in _recognizers.Values)
                {
                    try { recognizer.Dispose(); } catch { }
                }
            }

            _recognizers = null;
        }
    }

    public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] pngBytes, CancellationToken ct = default)
    {
        if (!PpOcrV5Service.IsReady)
            throw new OcrUnavailableException("PP-OCRv5 models are not installed.");

        IReadOnlyList<OcrWord> hints;
        try
        {
            hints = await new WinRtOcrEngine()
                .RecognizeAsync(pngBytes, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Log($"PP-OCRv5: Windows OCR hints unavailable ({ex.Message})");
            hints = Array.Empty<OcrWord>();
        }

        return await Task.Run<IReadOnlyList<OcrWord>>(() =>
        {
            lock (Sync)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    EnsureInitialized();

                    using var image = OpenCvSharp.Cv2.ImDecode(
                        pngBytes,
                        OpenCvSharp.ImreadModes.Color);
                    if (image.Empty())
                        return Array.Empty<OcrWord>();

                    var installed = PpOcrV5Service.InstalledRecognizerModels();
                    if (installed.Count == 0)
                        return Array.Empty<OcrWord>();

                    var rawHints = hints
                        .Where(h =>
                            !string.IsNullOrWhiteSpace(h.Text) &&
                            h.BoundsPixels.Width > 0 &&
                            h.BoundsPixels.Height > 0)
                        .ToArray();

                    double detectorScale = 1.0;
                    OpenCvSharp.Mat? scaledDetectorImage = null;
                    OpenCvSharp.Mat detectorInput = image;

                    int longestSide = Math.Max(image.Width, image.Height);
                    if (longestSide > 0 && longestSide < 1600)
                    {
                        detectorScale = Math.Min(2.0, 1600.0 / longestSide);
                        if (detectorScale > 1.01)
                        {
                            scaledDetectorImage = new OpenCvSharp.Mat();
                            OpenCvSharp.Cv2.Resize(
                                image,
                                scaledDetectorImage,
                                new OpenCvSharp.Size(),
                                detectorScale,
                                detectorScale,
                                OpenCvSharp.InterpolationFlags.Cubic);
                            detectorInput = scaledDetectorImage;
                        }
                    }

                    IReadOnlyList<Rect> detectorBounds;
                    try
                    {
                        var detected = _detector!.TextDetect(detectorInput).Data;
                        detectorBounds = GetDetectorBounds(
                            detected,
                            detectorScale);
                        detected?.ImgCropList?.Dispose();
                    }
                    finally
                    {
                        scaledDetectorImage?.Dispose();
                    }
                    ct.ThrowIfCancellationRequested();

                    var expandedHints = rawHints
                        .Select(h =>
                        {
                            var expanded = FindExpandedWordBounds(
                                image,
                                h.BoundsPixels,
                                rawHints,
                                detectorBounds);
                            return expanded is { } bounds
                                ? new OcrWord(h.Text, bounds)
                                : h;
                        })
                        .ToArray();

                    var usableHints = BuildVisualScaffold(
                        image,
                        expandedHints);

                    var hintLines = BuildHintLines(usableHints);
                    var globalModels = SelectModelsForTexts(
                        usableHints.Select(h => h.Text),
                        installed);

                    if (globalModels.Count == 0)
                        globalModels = installed;

                    var wordModels =
                        new IReadOnlyList<PpOcrV5RecognizerSpec>[usableHints.Length];

                    for (int lineIndex = 0; lineIndex < hintLines.Count; lineIndex++)
                    {
                        var line = hintLines[lineIndex];
                        var models = SelectModelsForTexts(
                            line.WordIndices.Select(i => usableHints[i].Text),
                            installed);

                        if (models.Count == 0)
                            models = globalModels;

                        foreach (int wordIndex in line.WordIndices)
                            wordModels[wordIndex] = models;
                    }

                    for (int i = 0; i < wordModels.Length; i++)
                    {
                        wordModels[i] ??= globalModels;

                        if (usableHints[i].Text.Count(char.IsLetterOrDigit) == 1)
                        {
                            var fallback = installed.FirstOrDefault(m =>
                                m.Language == LangRec.EN ||
                                m.Language == LangRec.LATIN);

                            if (fallback != null &&
                                wordModels[i].All(m =>
                                    !string.Equals(
                                        m.Key,
                                        fallback.Key,
                                        StringComparison.OrdinalIgnoreCase)))
                            {
                                wordModels[i] = wordModels[i]
                                    .Append(fallback)
                                    .ToArray();
                            }
                        }
                    }

                    var requiredModels = new Dictionary<string, PpOcrV5RecognizerSpec>(
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var models in wordModels)
                    {
                        foreach (var model in models)
                            requiredModels[model.Key] = model;
                    }

                    var recoveryRegions = BuildRecoveryRegions(
                        image,
                        detectorBounds,
                        usableHints,
                        globalModels,
                        installed);

                    foreach (var recovery in recoveryRegions)
                    {
                        foreach (var model in recovery.Models)
                            requiredModels[model.Key] = model;
                    }

                    var words = new List<OcrWord>();

                    if (usableHints.Length > 0)
                    {
                        var resultsByModel = RecognizeWordsPerModel(
                            image,
                            usableHints,
                            wordModels,
                            requiredModels.Values,
                            ct);

                        for (int i = 0; i < usableHints.Length; i++)
                        {
                            var hint = usableHints[i];
                            var lineScripts = new HashSet<string>(
                                wordModels[i]
                                    .Select(m => PrimaryScript(m.Language))
                                    .Where(s => s != "neutral"),
                                StringComparer.Ordinal);

                            string original = NormalizeWhitespace(hint.Text);
                            var wordScripts = CollectScripts([original]);
                            string chosen = original;
                            float chosenScore = ScoreOriginal(original, lineScripts);

                            foreach (var model in wordModels[i])
                            {
                                if (!resultsByModel.TryGetValue(model.Key, out var results) ||
                                    i >= results.Length)
                                {
                                    continue;
                                }

                                var result = results[i];
                                string candidate = NormalizeWhitespace(result.Label);
                                if (candidate.Length == 0 ||
                                    result.Score < MinimumTextScore)
                                {
                                    continue;
                                }

                                float score = ScoreCandidate(
                                    model,
                                    candidate,
                                    result.Score,
                                    lineScripts,
                                    wordScripts,
                                    original);

                                if (score > chosenScore)
                                {
                                    chosenScore = score;
                                    chosen = candidate;
                                }
                            }

                            if (chosen.Length == 0)
                                continue;

                            if (string.Equals(
                                    original,
                                    chosen,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                chosen = original;
                            }

                            chosen = PreferLineScriptLookalike(
                                chosen,
                                i,
                                wordModels[i],
                                resultsByModel,
                                hintLines,
                                usableHints);

                            chosen = ReconcileBoundaryPunctuation(
                                original,
                                chosen);

                            if (IsUnsupportedSingleGlyph(
                                    chosen,
                                    hint.BoundsPixels,
                                    detectorBounds,
                                    hintLines,
                                    i))
                            {
                                continue;
                            }

                            words.Add(new OcrWord(chosen, hint.BoundsPixels));
                        }
                    }
                    if (recoveryRegions.Count > 0)
                    {
                        using var recoveryCrops = BuildCrops(
                            image,
                            recoveryRegions.Select(r => r.Bounds).ToArray(),
                            padFraction: 0.08);

                        var recoveryModels = recoveryRegions
                            .SelectMany(r => r.Models)
                            .GroupBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                            .Select(g => g.First())
                            .ToArray();

                        var recoveryResults =
                            RecognizeWithModels(recoveryCrops, recoveryModels, ct);

                        for (int i = 0; i < recoveryRegions.Count; i++)
                        {
                            var recovery = recoveryRegions[i];
                            string bestText = string.Empty;
                            float bestScore = float.NegativeInfinity;
                            float bestConfidence = 0;

                            var regionScripts = new HashSet<string>(
                                recovery.Models
                                    .Select(m => PrimaryScript(m.Language))
                                    .Where(s => s != "neutral"),
                                StringComparer.Ordinal);

                            foreach (var model in recovery.Models)
                            {
                                if (!recoveryResults.TryGetValue(model.Key, out var results) ||
                                    i >= results.Length)
                                {
                                    continue;
                                }

                                var result = results[i];
                                string candidate = NormalizeWhitespace(result.Label);
                                if (candidate.Length == 0)
                                    continue;

                                float score = ScoreCandidate(
                                    model,
                                    candidate,
                                    result.Score,
                                    regionScripts,
                                    wordScripts: EmptyScripts,
                                    originalText: null);

                                if (score > bestScore)
                                {
                                    bestScore = score;
                                    bestConfidence = result.Score;
                                    bestText = candidate;
                                }
                            }

                            if (bestText.Length == 0 ||
                                bestConfidence < RecoveryTextScore)
                            {
                                continue;
                            }

                            int alphanumeric = bestText.Count(char.IsLetterOrDigit);
                            if (usableHints.Length > 0 && alphanumeric < 2)
                                continue;

                            if (recovery.PrefixRepairOnly)
                            {
                                TryRecoverMissingPrefixes(
                                    bestText,
                                    recovery.Bounds,
                                    words);
                                continue;
                            }

                            var recoveredWords = SplitByStrongVisualGaps(
                                image,
                                bestText,
                                recovery.Bounds);

                            string recoveredCore = string.Concat(
                                recoveredWords.Select(w =>
                                    AlphanumericCore(w.Text)));

                            bool nearDuplicate = recoveredCore.Length > 0 &&
                                words.Any(existing =>
                                {
                                    if (OverlapOverSmaller(
                                            recovery.Bounds,
                                            existing.BoundsPixels) < 0.55)
                                    {
                                        return false;
                                    }

                                    string existingCore =
                                        AlphanumericCore(existing.Text);
                                    if (existingCore.Length == 0)
                                        return false;

                                    bool contains =
                                        recoveredCore.Contains(
                                            existingCore,
                                            StringComparison.OrdinalIgnoreCase) ||
                                        existingCore.Contains(
                                            recoveredCore,
                                            StringComparison.OrdinalIgnoreCase);

                                    int tolerance = Math.Max(
                                        1,
                                        (int)Math.Ceiling(
                                            existingCore.Length * 0.25));

                                    return contains &&
                                        Math.Abs(
                                            recoveredCore.Length -
                                            existingCore.Length) <= tolerance;
                                });

                            if (!nearDuplicate)
                                words.AddRange(recoveredWords);
                        }
                    }

                    return Deduplicate(MergeAdjacentFragments(image, words));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("PP-OCRv5 failed; returning Windows OCR result", ex);
                    // Only a failing ONNX session needs a reload; heuristic bugs don't.
                    if (ex is Microsoft.ML.OnnxRuntime.OnnxRuntimeException or DllNotFoundException or SEHException)
                        Reset();
                    if (hints.Count > 0) return hints;
                    throw;
                }
            }
        }, ct).ConfigureAwait(false);
    }

    // Each model reads only the words assigned to it: a CJK line never goes through the EN model.
    // Result arrays stay indexed by word; slots a model didn't read are left default.
    private static Dictionary<string, RapidOCRSharpOnnx.Models.RecResult[]> RecognizeWordsPerModel(
        OpenCvSharp.Mat image,
        IReadOnlyList<OcrWord> words,
        IReadOnlyList<PpOcrV5RecognizerSpec>[] wordModels,
        IEnumerable<PpOcrV5RecognizerSpec> models,
        CancellationToken ct)
    {
        var results = new Dictionary<string, RapidOCRSharpOnnx.Models.RecResult[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models.DistinctBy(m => m.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            int[] indices = Enumerable.Range(0, words.Count)
                .Where(i => wordModels[i].Any(m => string.Equals(m.Key, model.Key, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            if (indices.Length == 0) continue;

            using var crops = BuildCrops(image, indices.Select(i => words[i].BoundsPixels).ToArray(), padFraction: 0.04);
            var subset = GetRecognizer(model).TextRecognize(crops).Data;
            var full = new RapidOCRSharpOnnx.Models.RecResult[words.Count];
            for (int k = 0; k < indices.Length && k < subset.Length; k++)
                full[indices[k]] = subset[k];
            results[model.Key] = full;
        }
        return results;
    }

    private static Dictionary<string, RapidOCRSharpOnnx.Models.RecResult[]>
        RecognizeWithModels(
            DisposableList<RapidOCRSharpOnnx.Inference.PPOCR_Rec.Models.ImageIndex> crops,
            IEnumerable<PpOcrV5RecognizerSpec> models,
            CancellationToken ct)
    {
        var results =
            new Dictionary<string, RapidOCRSharpOnnx.Models.RecResult[]>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var model in models
                     .GroupBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.First()))
        {
            ct.ThrowIfCancellationRequested();
            results[model.Key] = GetRecognizer(model)
                .TextRecognize(crops)
                .Data;
        }

        return results;
    }

    private static DisposableList<RapidOCRSharpOnnx.Inference.PPOCR_Rec.Models.ImageIndex>
        BuildCrops(
            OpenCvSharp.Mat image,
            IReadOnlyList<Rect> bounds,
            double padFraction)
    {
        var crops =
            new DisposableList<RapidOCRSharpOnnx.Inference.PPOCR_Rec.Models.ImageIndex>();

        for (int i = 0; i < bounds.Count; i++)
        {
            var cropRect = ToPaddedCropRect(
                bounds[i],
                image.Width,
                image.Height,
                padFraction);

            using var roi = new OpenCvSharp.Mat(image, cropRect);
            var crop = roi.Clone();
            crops.Add(
                new RapidOCRSharpOnnx.Inference.PPOCR_Rec.Models.ImageIndex(
                    crop,
                    i));
        }

        return crops;
    }

    private static OpenCvSharp.Rect ToPaddedCropRect(
        Rect bounds,
        int imageWidth,
        int imageHeight,
        double padFraction)
    {
        double pad = Math.Max(1, bounds.Height * padFraction);

        int x0 = Math.Clamp(
            (int)Math.Floor(bounds.X - pad),
            0,
            imageWidth - 1);
        int y0 = Math.Clamp(
            (int)Math.Floor(bounds.Y - pad * 0.5),
            0,
            imageHeight - 1);
        int x1 = Math.Clamp(
            (int)Math.Ceiling(bounds.X + bounds.Width + pad),
            x0 + 1,
            imageWidth);
        int y1 = Math.Clamp(
            (int)Math.Ceiling(bounds.Y + bounds.Height + pad * 0.5),
            y0 + 1,
            imageHeight);

        return new OpenCvSharp.Rect(
            x0,
            y0,
            x1 - x0,
            y1 - y0);
    }

    private static IReadOnlyList<PpOcrV5RecognizerSpec> SelectModelsForTexts(
        IEnumerable<string> texts,
        IReadOnlyList<PpOcrV5RecognizerSpec> installed)
    {
        var scripts = CollectScripts(texts);
        if (scripts.Count == 0)
            return Array.Empty<PpOcrV5RecognizerSpec>();

        var selected = new List<PpOcrV5RecognizerSpec>();

        foreach (string script in scripts)
        {
            var model = SelectModelForScript(script, installed);
            if (model != null &&
                selected.All(existing =>
                    !string.Equals(
                        existing.Key,
                        model.Key,
                        StringComparison.OrdinalIgnoreCase)))
            {
                selected.Add(model);
            }
        }

        return selected;
    }

    private static PpOcrV5RecognizerSpec? SelectModelForScript(
        string script,
        IReadOnlyList<PpOcrV5RecognizerSpec> installed)
    {
        LangRec[] preference = script switch
        {
            "ascii" => [LangRec.EN, LangRec.LATIN],
            "latin" => [LangRec.LATIN, LangRec.EN],
            "cyrillic" => [LangRec.ESLAV, LangRec.CYRILLIC],
            "cjk" => [LangRec.CH],
            "korean" => [LangRec.KOREAN],
            "th" => [LangRec.TH],
            "el" => [LangRec.EL],
            "arabic" => [LangRec.ARABIC],
            "devanagari" => [LangRec.DEVANAGARI],
            "ta" => [LangRec.TA],
            "te" => [LangRec.TE],
            _ => [],
        };

        foreach (var language in preference)
        {
            var model = installed.FirstOrDefault(m => m.Language == language);
            if (model != null)
                return model;
        }

        return null;
    }

    private static float ScoreOriginal(
        string text,
        HashSet<string> lineScripts)
    {
        if (text.Length == 0)
            return float.NegativeInfinity;

        var scripts = CollectScripts([text]);
        if (scripts.Count == 0)
            return 0.66f;

        if (lineScripts.Count == 0)
            return 0.62f;

        int overlap = scripts.Count(lineScripts.Contains);
        float agreement = overlap / (float)scripts.Count;

        return 0.58f + 0.18f * agreement;
    }

    private static float ScoreCandidate(
        PpOcrV5RecognizerSpec model,
        string text,
        float confidence,
        HashSet<string> lineScripts,
        HashSet<string> wordScripts,
        string? originalText)
    {
        var outputScripts = CollectScripts([text]);
        float score = confidence;

        if (outputScripts.Count > 0)
        {
            int modelMatches = outputScripts.Count(script =>
                ModelMatchesScript(model.Language, script));
            score += 0.08f * modelMatches / outputScripts.Count;
        }

        if (lineScripts.Count > 0 && outputScripts.Count > 0)
        {
            int overlap = outputScripts.Count(lineScripts.Contains);
            float agreement = overlap / (float)outputScripts.Count;
            score += 0.06f * agreement;

            if (overlap == 0 && confidence < 0.92f)
                score -= 0.08f;
        }

        int originalLetters = string.IsNullOrWhiteSpace(originalText)
            ? 0
            : originalText.Count(char.IsLetter);

        if (originalLetters >= 1 &&
            wordScripts.Count > 0 &&
            outputScripts.Count > 0)
        {
            int overlap = outputScripts.Count(wordScripts.Contains);
            float agreement = overlap / (float)outputScripts.Count;
            score += 0.10f * agreement;

            int foreignScripts = outputScripts.Count(script =>
                !wordScripts.Contains(script));
            if (wordScripts.Count == 1 && foreignScripts > 0 && overlap > 0)
                score -= 0.42f;

            if (overlap == 0)
            {
                CaseProfile originalCase = GetCaseProfile(originalText!);
                bool strongOriginalScript =
                    originalLetters >= 3 ||
                    originalCase != CaseProfile.Upper;

                score -= strongOriginalScript
                    ? 0.42f
                    : confidence < 0.94f ? 0.12f : 0;
            }
        }

        if (!string.IsNullOrWhiteSpace(originalText))
        {
            int originalLength = CompactLength(originalText);
            int candidateLength = CompactLength(text);

            if (originalLength > 0)
            {
                double ratio = candidateLength / (double)originalLength;
                if (ratio < 0.45 || ratio > 2.20)
                    score -= 0.12f;
            }

            score += SingleCharacterAgreementAdjustment(
                originalText,
                text,
                confidence);

            score += CaseAgreementAdjustment(
                originalText,
                text);
        }

        return score;
    }

    private enum CaseProfile
    {
        None,
        Lower,
        Upper,
        Title,
        Mixed,
    }

    private static float SingleCharacterAgreementAdjustment(
        string original,
        string candidate,
        float confidence)
    {
        var originalCore = original
            .Where(char.IsLetterOrDigit)
            .ToArray();
        var candidateCore = candidate
            .Where(char.IsLetterOrDigit)
            .ToArray();

        if (originalCore.Length != 1 ||
            candidateCore.Length != 1)
        {
            return 0;
        }

        char a = originalCore[0];
        char b = candidateCore[0];

        bool aLetter = char.IsLetter(a);
        bool bLetter = char.IsLetter(b);
        bool aDigit = char.IsDigit(a);
        bool bDigit = char.IsDigit(b);

        if (aLetter && bLetter)
        {
            string aScript = GetLetterScript(a);
            string bScript = GetLetterScript(b);

            if (aScript != "neutral" &&
                bScript != "neutral" &&
                !string.Equals(
                    aScript,
                    bScript,
                    StringComparison.Ordinal))
            {
                return -0.45f;
            }

            return 0.03f;
        }

        if (aLetter && bDigit && confidence >= 0.75f)
            return 0.10f;

        if (aDigit && bLetter)
            return -0.12f;

        if (aDigit && bDigit)
            return 0.03f;

        return 0;
    }

    private static float CaseAgreementAdjustment(
        string original,
        string candidate)
    {
        if (original.Count(char.IsLetter) < 2 ||
            candidate.Count(char.IsLetter) < 2)
        {
            return 0;
        }

        var originalScripts = CollectScripts([original]);
        var candidateScripts = CollectScripts([candidate]);
        if (originalScripts.Count == 0 ||
            candidateScripts.Count == 0 ||
            !originalScripts.Overlaps(candidateScripts))
        {
            return 0;
        }

        CaseProfile a = GetCaseProfile(original);
        CaseProfile b = GetCaseProfile(candidate);

        if (a == CaseProfile.None ||
            b == CaseProfile.None ||
            a == CaseProfile.Mixed ||
            b == CaseProfile.Mixed)
        {
            return 0;
        }

        return a == b ? 0.03f : -0.05f;
    }

    private static CaseProfile GetCaseProfile(string text)
    {
        var letters = text
            .Where(char.IsLetter)
            .ToArray();

        if (letters.Length == 0)
            return CaseProfile.None;

        bool allUpper = letters.All(char.IsUpper);
        bool allLower = letters.All(char.IsLower);

        if (allUpper)
            return CaseProfile.Upper;
        if (allLower)
            return CaseProfile.Lower;

        if (char.IsUpper(letters[0]) &&
            letters.Skip(1).All(char.IsLower))
        {
            return CaseProfile.Title;
        }

        return CaseProfile.Mixed;
    }

    private static string ReconcileBoundaryPunctuation(
        string original,
        string candidate)
    {
        if (string.IsNullOrEmpty(candidate) ||
            string.IsNullOrEmpty(original))
        {
            return candidate;
        }

        string result = candidate;

        // When both engines see boundary punctuation but disagree, keep the hint's ("[Beta]", not "Beta").
        int originalPrefix = LeadingNonAlphanumericLength(original);
        int candidatePrefix = LeadingNonAlphanumericLength(result);

        if (originalPrefix > 0 &&
            candidatePrefix > 0 &&
            candidatePrefix < result.Length &&
            !string.Equals(
                original[..originalPrefix],
                result[..candidatePrefix],
                StringComparison.Ordinal))
        {
            result = original[..originalPrefix] + result[candidatePrefix..];
        }

        int originalSuffix = TrailingNonAlphanumericLength(original);
        int candidateSuffix = TrailingNonAlphanumericLength(result);

        if (originalSuffix > 0 &&
            candidateSuffix > 0 &&
            candidateSuffix < result.Length &&
            !string.Equals(
                original[^originalSuffix..],
                result[^candidateSuffix..],
                StringComparison.Ordinal))
        {
            result = result[..^candidateSuffix] + original[^originalSuffix..];
        }

        return result.Trim();
    }

    // Cyrillic and Latin letters that render identically in UI fonts.
    private static readonly Dictionary<char, char> LookalikeToLatin = new()
    {
        ['А'] = 'A', ['В'] = 'B', ['Е'] = 'E', ['К'] = 'K', ['М'] = 'M', ['Н'] = 'H', ['О'] = 'O',
        ['Р'] = 'P', ['С'] = 'C', ['Т'] = 'T', ['Х'] = 'X', ['У'] = 'Y', ['І'] = 'I', ['Ј'] = 'J', ['Ѕ'] = 'S',
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['у'] = 'y', ['х'] = 'x',
        ['і'] = 'i', ['ј'] = 'j', ['ѕ'] = 's', ['п'] = 'n', ['к'] = 'k',
    };

    private static string LookalikeSkeleton(string text)
    {
        var chars = text.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (LookalikeToLatin.TryGetValue(chars[i], out var latin)) chars[i] = latin;
        return new string(chars);
    }

    // "latin" / "cyrillic" when every letter is from that script, otherwise null.
    private static string? PureLetterScript(string text)
    {
        bool latin = false, cyrillic = false, other = false;
        foreach (char c in text)
        {
            if (!char.IsLetter(c)) continue;
            if (c <= '\u024F') latin = true;
            else if (c is >= '\u0400' and <= '\u04FF') cyrillic = true;
            else other = true;
        }
        if (other || latin == cyrillic) return null;
        return latin ? "latin" : "cyrillic";
    }

    /// <summary>Windows OCR with several profile languages reads "Open" as Cyrillic "Ореп" (and the
    /// reverse). When a model produced the same glyphs in the script the rest of the line uses, take it.</summary>
    private static string PreferLineScriptLookalike(
        string chosen,
        int wordIndex,
        IReadOnlyList<PpOcrV5RecognizerSpec> models,
        Dictionary<string, RapidOCRSharpOnnx.Models.RecResult[]> resultsByModel,
        IReadOnlyList<HintLine> lines,
        IReadOnlyList<OcrWord> hints)
    {
        string? chosenScript = PureLetterScript(chosen);
        if (chosenScript == null) return chosen;

        var line = lines.FirstOrDefault(l => l.WordIndices.Contains(wordIndex));
        if (line == null) return chosen;
        int latin = 0, cyrillic = 0;
        foreach (int other in line.WordIndices)
        {
            if (other == wordIndex || hints[other].Text.Count(char.IsLetter) < 2) continue;
            switch (PureLetterScript(hints[other].Text))
            {
                case "latin": latin++; break;
                case "cyrillic": cyrillic++; break;
            }
        }
        string? lineScript =
            latin >= 2 && latin >= 2 * cyrillic ? "latin" :
            cyrillic >= 2 && cyrillic >= 2 * latin ? "cyrillic" : null;
        if (lineScript == null || lineScript == chosenScript) return chosen;

        string skeleton = LookalikeSkeleton(chosen);
        foreach (var model in models)
        {
            if (!resultsByModel.TryGetValue(model.Key, out var results) || wordIndex >= results.Length)
                continue;
            string candidate = NormalizeWhitespace(results[wordIndex].Label);
            if (candidate.Length > 0 &&
                PureLetterScript(candidate) == lineScript &&
                string.Equals(LookalikeSkeleton(candidate), skeleton, StringComparison.Ordinal))
            {
                return candidate;
            }
        }
        return chosen;
    }

    private static int LeadingNonAlphanumericLength(string text)
    {
        int length = 0;
        while (length < text.Length &&
               !char.IsLetterOrDigit(text[length]))
        {
            length++;
        }

        return length;
    }

    private static int TrailingNonAlphanumericLength(string text)
    {
        int length = 0;
        while (length < text.Length &&
               !char.IsLetterOrDigit(text[text.Length - 1 - length]))
        {
            length++;
        }

        return length;
    }

    private static bool ModelMatchesScript(LangRec language, string script) =>
        language switch
        {
            LangRec.EN => script == "ascii",
            LangRec.LATIN => script is "ascii" or "latin",
            LangRec.CYRILLIC or LangRec.ESLAV => script == "cyrillic",
            LangRec.CH => script == "cjk",
            LangRec.KOREAN => script == "korean",
            LangRec.TH => script == "th",
            LangRec.EL => script == "el",
            LangRec.ARABIC => script == "arabic",
            LangRec.DEVANAGARI => script == "devanagari",
            LangRec.TA => script == "ta",
            LangRec.TE => script == "te",
            _ => false,
        };

    private static string PrimaryScript(LangRec language) =>
        language switch
        {
            LangRec.EN => "ascii",
            LangRec.LATIN => "latin",
            LangRec.CYRILLIC or LangRec.ESLAV => "cyrillic",
            LangRec.CH => "cjk",
            LangRec.KOREAN => "korean",
            LangRec.TH => "th",
            LangRec.EL => "el",
            LangRec.ARABIC => "arabic",
            LangRec.DEVANAGARI => "devanagari",
            LangRec.TA => "ta",
            LangRec.TE => "te",
            _ => "neutral",
        };

    private static HashSet<string> CollectScripts(IEnumerable<string> texts)
    {
        var scripts = new HashSet<string>(StringComparer.Ordinal);

        foreach (string text in texts)
        {
            foreach (char ch in text)
            {
                if (!char.IsLetter(ch))
                    continue;

                string script = GetLetterScript(ch);
                if (script != "neutral")
                    scripts.Add(script);
            }
        }

        return scripts;
    }

    private static Rect? FindExpandedWordBounds(
        OpenCvSharp.Mat image,
        Rect word,
        IReadOnlyList<OcrWord> words,
        IReadOnlyList<Rect> detectorBounds)
    {
        Rect? best = null;
        double bestArea = double.PositiveInfinity;

        foreach (var detector in detectorBounds)
        {
            if (VerticalOverlapRatio(detector, word) < 0.50)
                continue;

            if (OverlapOverSmaller(detector, word) < 0.30)
                continue;

            if (detector.Width > word.Width * 2.5 ||
                detector.Height > word.Height * 2.25)
            {
                continue;
            }

            int centersInside = 0;
            foreach (var other in words)
            {
                var b = other.BoundsPixels;
                double cx = b.X + b.Width * 0.5;
                double cy = b.Y + b.Height * 0.5;

                if (cx >= detector.X - 2 &&
                    cx <= detector.X + detector.Width + 2 &&
                    cy >= detector.Y - 2 &&
                    cy <= detector.Y + detector.Height + 2)
                {
                    centersInside++;
                }
            }

            if (centersInside != 1)
                continue;

            if (FindStrongVisualSegments(image, detector).Count >= 2)
                continue;

            Rect union = UnionBounds(word, detector);
            double extraWidth = union.Width - word.Width;
            if (extraWidth < Math.Max(2, word.Height * 0.12))
                continue;

            // Growing over a word-sized gap would swallow a neighbouring word the hints missed.
            int wordSpace = Math.Max(3, (int)Math.Round(word.Height * 0.3));
            if (union.X < word.X - 1 &&
                LongestBlankRun(image, new Rect(union.X, word.Y, word.X - union.X + 2, word.Height), interiorOnly: true) >= wordSpace)
                continue;
            double wordRight = word.X + word.Width;
            double unionRight = union.X + union.Width;
            if (unionRight > wordRight + 1 &&
                LongestBlankRun(image, new Rect(wordRight - 2, word.Y, unionRight - wordRight + 2, word.Height), interiorOnly: true) >= wordSpace)
                continue;

            double area = detector.Width * detector.Height;
            if (area < bestArea)
            {
                bestArea = area;
                best = union;
            }
        }

        return best;
    }

    private static OcrWord[] BuildVisualScaffold(
        OpenCvSharp.Mat image,
        IReadOnlyList<OcrWord> hints)
    {
        if (hints.Count == 0)
            return Array.Empty<OcrWord>();

        var lines = new List<List<OcrWord>>();

        foreach (var hint in hints
                     .Where(h =>
                         !string.IsNullOrWhiteSpace(h.Text) &&
                         h.BoundsPixels.Width > 0 &&
                         h.BoundsPixels.Height > 0)
                     .OrderBy(h =>
                         h.BoundsPixels.Y +
                         h.BoundsPixels.Height * 0.5)
                     .ThenBy(h => h.BoundsPixels.X))
        {
            List<OcrWord>? target = null;
            foreach (var line in lines)
            {
                if (line.Any(existing =>
                    SameVisualLine(
                        existing.BoundsPixels,
                        hint.BoundsPixels)))
                {
                    target = line;
                    break;
                }
            }

            if (target == null)
            {
                target = new List<OcrWord>();
                lines.Add(target);
            }

            target.Add(new OcrWord(
                NormalizeWhitespace(hint.Text),
                hint.BoundsPixels));
        }

        var result = new List<OcrWord>();

        foreach (var line in lines)
        {
            line.Sort((a, b) =>
                a.BoundsPixels.X.CompareTo(b.BoundsPixels.X));

            for (int i = 0; i < line.Count; i++)
            {
                var current = line[i];

                while (i + 1 < line.Count &&
                       ShouldMergeHintFragments(
                           image,
                           current.BoundsPixels,
                           line[i + 1].BoundsPixels))
                {
                    var next = line[++i];
                    current = new OcrWord(
                        NormalizeWhitespace(current.Text) +
                        NormalizeWhitespace(next.Text),
                        UnionBounds(
                            current.BoundsPixels,
                            next.BoundsPixels));
                }

                result.Add(current);
            }
        }

        return result
            .OrderBy(w => w.BoundsPixels.Y)
            .ThenBy(w => w.BoundsPixels.X)
            .ToArray();
    }

    private static bool ShouldMergeHintFragments(
        OpenCvSharp.Mat image,
        Rect left,
        Rect right)
    {
        if (!SameVisualLine(left, right))
            return false;

        double gap = right.X - (left.X + left.Width);
        double height = Math.Max(left.Height, right.Height);

        if (gap < Math.Max(4, height * 0.45) ||
            gap > Math.Max(28, height * 2.25))
            return false;

        int x0 = Math.Clamp(
            (int)Math.Floor(left.X + left.Width),
            0,
            image.Width - 1);
        int x1 = Math.Clamp(
            (int)Math.Ceiling(right.X),
            x0 + 1,
            image.Width);
        int y0 = Math.Clamp(
            (int)Math.Floor(Math.Min(left.Y, right.Y)) - 2,
            0,
            image.Height - 1);
        int y1 = Math.Clamp(
            (int)Math.Ceiling(
                Math.Max(
                    left.Y + left.Height,
                    right.Y + right.Height)) + 2,
            y0 + 1,
            image.Height);

        if (x1 <= x0 || y1 <= y0)
            return false;

        try
        {
            var rect = new OpenCvSharp.Rect(x0, y0, x1 - x0, y1 - y0);
            var columns = InkPerColumn(image, rect);
            int maxInkPerBlankColumn = Math.Max(0, (int)Math.Round(rect.Height * 0.03));
            int longestBlank = 0;
            int currentBlank = 0;
            int foreground = 0;
            foreach (int ink in columns)
            {
                foreground += ink;
                currentBlank = ink <= maxInkPerBlankColumn ? currentBlank + 1 : 0;
                longestBlank = Math.Max(longestBlank, currentBlank);
            }

            int wordGap = Math.Max(
                3,
                (int)Math.Round(height * 0.28));
            if (longestBlank >= wordGap)
                return false;

            int area = rect.Width * rect.Height;
            return foreground >= Math.Max(
                3,
                (int)Math.Round(area * 0.025));
        }
        catch
        {
            return false;
        }
    }
    private static List<HintLine> BuildHintLines(
        IReadOnlyList<OcrWord> hints)
    {
        var lines = new List<List<int>>();

        foreach (int index in Enumerable.Range(0, hints.Count)
                     .OrderBy(i =>
                         hints[i].BoundsPixels.Y +
                         hints[i].BoundsPixels.Height * 0.5)
                     .ThenBy(i => hints[i].BoundsPixels.X))
        {
            var bounds = hints[index].BoundsPixels;
            List<int>? target = null;

            foreach (var line in lines)
            {
                if (line.Any(i =>
                    SameVisualLine(
                        hints[i].BoundsPixels,
                        bounds)))
                {
                    target = line;
                    break;
                }
            }

            if (target == null)
            {
                target = new List<int>();
                lines.Add(target);
            }

            target.Add(index);
        }

        var result = new List<HintLine>(lines.Count);

        foreach (var line in lines)
        {
            line.Sort((a, b) =>
                hints[a].BoundsPixels.X.CompareTo(
                    hints[b].BoundsPixels.X));

            Rect bounds = hints[line[0]].BoundsPixels;
            for (int i = 1; i < line.Count; i++)
                bounds = UnionBounds(bounds, hints[line[i]].BoundsPixels);

            result.Add(new HintLine(line.ToArray(), bounds));
        }

        return result;
    }

    private static IReadOnlyList<Rect> GetDetectorBounds(
        RapidOCRSharpOnnx.Inference.PPOCR_Det.DetResult? detected,
        double inputScale)
    {
        if (detected?.DetItems == null ||
            detected.DetItems.Length == 0)
        {
            return Array.Empty<Rect>();
        }

        double inverse = inputScale > 0 ? 1.0 / inputScale : 1.0;

        return detected.DetItems
            .Select(item => MapBoxToOriginal(detected, item.Box))
            .Select(b => new Rect(
                b.X * inverse,
                b.Y * inverse,
                b.Width * inverse,
                b.Height * inverse))
            .Where(b => b.Width > 0 && b.Height > 0)
            .ToArray();
    }

    private static List<RecoveryRegion> BuildRecoveryRegions(
        OpenCvSharp.Mat image,
        IReadOnlyList<Rect> detectorBounds,
        IReadOnlyList<OcrWord> hints,
        IReadOnlyList<PpOcrV5RecognizerSpec> globalModels,
        IReadOnlyList<PpOcrV5RecognizerSpec> installed)
    {
        var regions = new List<RecoveryRegion>();

        foreach (var detector in detectorBounds)
        {
            var visualSegments = FindStrongVisualSegments(image, detector);

            var candidates = visualSegments.Count >= 2
                ? visualSegments
                    .Select(segment => new Rect(
                        detector.X + segment.X,
                        detector.Y,
                        segment.Width,
                        detector.Height))
                    .ToArray()
                : [detector];

            foreach (var candidate in candidates)
            {
                double coveredWidth = HorizontalCoverageRatio(
                    candidate,
                    hints.Select(h => h.BoundsPixels));

                if (coveredWidth >= 0.82)
                    continue;

                bool prefixRepairOnly = coveredWidth >= 0.68;

                var lineHints = hints
                    .Where(h => SameVisualLine(candidate, h.BoundsPixels))
                    .ToArray();

                var models = SelectModelsForTexts(
                    lineHints.Select(h => h.Text),
                    installed);

                if (models.Count == 0)
                    models = globalModels.Count > 0 ? globalModels : installed;

                // Ink Windows OCR skipped is often in a script its languages don't cover, so the
                // line's hints say nothing about it: also try the Latin and CJK models.
                models = models
                    .Concat(new[] { LangRec.EN, LangRec.CH }
                        .Select(lang => installed.FirstOrDefault(m => m.Language == lang))
                        .OfType<PpOcrV5RecognizerSpec>())
                    .DistinctBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                regions.Add(new RecoveryRegion(
                    candidate,
                    models,
                    prefixRepairOnly));
            }
        }

        return MergeRecoveryRegions(regions);
    }
    private static double HorizontalCoverageRatio(
        Rect target,
        IEnumerable<Rect> candidates)
    {
        if (target.Width <= 0)
            return 0;

        var intervals = candidates
            .Where(candidate =>
                VerticalOverlapRatio(target, candidate) >= 0.40)
            .Select(candidate => (
                Start: Math.Max(target.X, candidate.X),
                End: Math.Min(
                    target.X + target.Width,
                    candidate.X + candidate.Width)))
            .Where(interval => interval.End > interval.Start)
            .OrderBy(interval => interval.Start)
            .ToArray();

        if (intervals.Length == 0)
            return 0;

        double covered = 0;
        double start = intervals[0].Start;
        double end = intervals[0].End;

        for (int i = 1; i < intervals.Length; i++)
        {
            if (intervals[i].Start <= end)
            {
                end = Math.Max(end, intervals[i].End);
                continue;
            }

            covered += end - start;
            start = intervals[i].Start;
            end = intervals[i].End;
        }

        covered += end - start;
        return Math.Clamp(covered / target.Width, 0, 1);
    }

    private static List<RecoveryRegion> MergeRecoveryRegions(
        IReadOnlyList<RecoveryRegion> regions)
    {
        var result = new List<RecoveryRegion>();

        foreach (var region in regions
                     .Where(r => r.Bounds.Width > 0 && r.Bounds.Height > 0)
                     .OrderBy(r => r.Bounds.Y)
                     .ThenBy(r => r.Bounds.X))
        {
            bool duplicate = result.Any(existing =>
                OverlapOverSmaller(
                    existing.Bounds,
                    region.Bounds) >= 0.80);

            if (!duplicate)
                result.Add(region);
        }

        return result;
    }

    private static bool TryRecoverMissingPrefixes(
        string recognizedText,
        Rect recoveryBounds,
        List<OcrWord> words)
    {
        string compact = RemoveWhitespace(
            NormalizeWhitespace(recognizedText));
        if (compact.Length < 3 || words.Count < 2)
            return false;

        var candidates = Enumerable.Range(0, words.Count)
            .Where(i =>
            {
                var bounds = words[i].BoundsPixels;
                double horizontalOverlap =
                    Math.Min(
                        recoveryBounds.X + recoveryBounds.Width,
                        bounds.X + bounds.Width) -
                    Math.Max(recoveryBounds.X, bounds.X);

                return horizontalOverlap > 0 &&
                    VerticalOverlapRatio(recoveryBounds, bounds) >= 0.50;
            })
            .OrderBy(i => words[i].BoundsPixels.X)
            .ToArray();

        if (candidates.Length < 2)
            return false;

        var matches =
            new List<(int WordIndex, int Start, int End)>();
        int searchStart = 0;

        foreach (int wordIndex in candidates)
        {
            string token = RemoveWhitespace(
                NormalizeWhitespace(words[wordIndex].Text));
            if (token.Length == 0)
                continue;

            int start = compact.IndexOf(
                token,
                searchStart,
                StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                continue;

            matches.Add((
                wordIndex,
                start,
                start + token.Length));
            searchStart = start + token.Length;
        }

        if (matches.Count < 2 ||
            matches[0].Start != 0 ||
            matches[^1].End != compact.Length)
        {
            return false;
        }

        int gapMatchIndex = -1;
        string prefix = string.Empty;

        for (int i = 1; i < matches.Count; i++)
        {
            int gapLength = matches[i].Start - matches[i - 1].End;
            if (gapLength < 0)
                return false;
            if (gapLength == 0)
                continue;
            if (gapMatchIndex >= 0)
                return false;

            gapMatchIndex = i;
            prefix = compact.Substring(
                matches[i - 1].End,
                gapLength);
        }

        if (gapMatchIndex != matches.Count - 1 ||
            prefix.Length == 0 ||
            prefix.Any(ch => !char.IsLetterOrDigit(ch)))
        {
            return false;
        }

        var previousMatch = matches[gapMatchIndex - 1];
        var targetMatch = matches[gapMatchIndex];
        var previous = words[previousMatch.WordIndex];
        var target = words[targetMatch.WordIndex];

        string prefixCore = AlphanumericCore(prefix);
        string targetCore = AlphanumericCore(target.Text);
        if (prefixCore.Length == 0 ||
            targetCore.Length < 2 ||
            prefixCore.Length > targetCore.Length)
        {
            return false;
        }

        var prefixScripts = CollectScripts([prefix]);
        var targetScripts = CollectScripts([target.Text]);
        if (prefixScripts.Count > 0 &&
            targetScripts.Count > 0 &&
            !prefixScripts.Overlaps(targetScripts))
        {
            return false;
        }

        var previousBounds = previous.BoundsPixels;
        var targetBounds = target.BoundsPixels;
        double height = Math.Max(
            previousBounds.Height,
            targetBounds.Height);
        double gapPixels =
            targetBounds.X -
            (previousBounds.X + previousBounds.Width);
        double characterWidth =
            targetBounds.Width / Math.Max(1, targetCore.Length);
        double expectedPrefixWidth =
            characterWidth * prefixCore.Length;

        if (gapPixels < -height * 0.15 ||
            gapPixels >
                expectedPrefixWidth * 1.55 +
                height * 0.60)
        {
            return false;
        }

        double projectedX =
            recoveryBounds.X +
            recoveryBounds.Width *
            previousMatch.End /
            (double)compact.Length;
        double projectedPrefixWidth =
            targetBounds.X - projectedX;

        if (projectedPrefixWidth <
                Math.Max(1, expectedPrefixWidth * 0.35) ||
            projectedPrefixWidth >
                expectedPrefixWidth * 1.80 +
                height * 0.50)
        {
            return false;
        }

        double previousRight =
            previousBounds.X + previousBounds.Width;
        double newX = Math.Max(
            previousRight,
            Math.Min(targetBounds.X, projectedX));
        double targetRight =
            targetBounds.X + targetBounds.Width;

        if (targetRight <= newX)
            return false;

        words[targetMatch.WordIndex] = new OcrWord(
            prefix + target.Text,
            new Rect(
                newX,
                targetBounds.Y,
                targetRight - newX,
                targetBounds.Height));
        return true;
    }

    private static IReadOnlyList<OcrWord> SplitByStrongVisualGaps(
        OpenCvSharp.Mat image,
        string text,
        Rect bounds)
    {
        string normalized = NormalizeWhitespace(text);
        if (normalized.Length == 0)
            return Array.Empty<OcrWord>();

        var segments = FindStrongVisualSegments(image, bounds);
        if (segments.Count < 2)
            return [new OcrWord(normalized, bounds)];

        // Trust the recognizer's own spaces when they agree with the visual gaps.
        string[] tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string[] textSegments;
        if (tokens.Length == segments.Count)
        {
            textSegments = tokens;
        }
        else
        {
            string compact = RemoveWhitespace(normalized);
            int[] weights = segments
                .Select(segment => Math.Max(1, segment.Width))
                .ToArray();
            textSegments = SplitTextByWeights(compact, weights);
        }
        var result = new List<OcrWord>(textSegments.Length);

        for (int i = 0; i < textSegments.Length && i < segments.Count; i++)
        {
            if (textSegments[i].Length == 0)
                continue;

            var segment = segments[i];
            result.Add(new OcrWord(
                textSegments[i],
                new Rect(
                    bounds.X + segment.X,
                    bounds.Y,
                    segment.Width,
                    bounds.Height)));
        }

        return result.Count > 0
            ? result
            : [new OcrWord(normalized, bounds)];
    }

    private static IReadOnlyList<OpenCvSharp.Rect> FindStrongVisualSegments(
        OpenCvSharp.Mat image,
        Rect bounds)
    {
        int x0 = Math.Clamp((int)Math.Floor(bounds.X), 0, image.Width - 1);
        int y0 = Math.Clamp((int)Math.Floor(bounds.Y), 0, image.Height - 1);
        int x1 = Math.Clamp(
            (int)Math.Ceiling(bounds.X + bounds.Width),
            x0 + 1,
            image.Width);
        int y1 = Math.Clamp(
            (int)Math.Ceiling(bounds.Y + bounds.Height),
            y0 + 1,
            image.Height);

        int width = x1 - x0;
        int height = y1 - y0;
        if (width < 12 || height < 6)
            return Array.Empty<OpenCvSharp.Rect>();

        try
        {
            var columns = InkPerColumn(image, new OpenCvSharp.Rect(x0, y0, width, height));
            int minGap = Math.Max(5, (int)Math.Round(height * 0.40));
            int maxInk = Math.Max(0, (int)Math.Round(height * 0.03));
            int edgeGuard = Math.Max(1, (int)Math.Round(width * 0.04));

            var gaps = new List<(int Start, int End)>();
            int gapStart = -1;

            for (int x = edgeGuard; x < width - edgeGuard; x++)
            {
                int ink = columns[x];

                if (ink <= maxInk)
                {
                    if (gapStart < 0)
                        gapStart = x;
                }
                else if (gapStart >= 0)
                {
                    if (x - gapStart >= minGap)
                        gaps.Add((gapStart, x));
                    gapStart = -1;
                }
            }

            if (gapStart >= 0 &&
                width - edgeGuard - gapStart >= minGap)
            {
                gaps.Add((gapStart, width - edgeGuard));
            }

            if (gaps.Count == 0)
                return Array.Empty<OpenCvSharp.Rect>();

            var result = new List<OpenCvSharp.Rect>();
            int start = 0;

            foreach (var gap in gaps)
            {
                int end = gap.Start + (gap.End - gap.Start) / 2;
                if (end - start >= 2)
                    result.Add(new OpenCvSharp.Rect(start, 0, end - start, height));

                start = end;
            }

            if (width - start >= 2)
                result.Add(new OpenCvSharp.Rect(start, 0, width - start, height));

            return result.Count >= 2
                ? result
                : Array.Empty<OpenCvSharp.Rect>();
        }
        catch
        {
            return Array.Empty<OpenCvSharp.Rect>();
        }
    }

    /// <summary>Ink pixel count per column after Otsu binarization (text polarity normalized).</summary>
    private static int[] InkPerColumn(OpenCvSharp.Mat image, OpenCvSharp.Rect rect)
    {
        using var crop = new OpenCvSharp.Mat(image, rect);
        using var gray = new OpenCvSharp.Mat();
        using var binary = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.CvtColor(crop, gray, OpenCvSharp.ColorConversionCodes.BGR2GRAY);
        OpenCvSharp.Cv2.Threshold(gray, binary, 0, 1,
            OpenCvSharp.ThresholdTypes.Binary | OpenCvSharp.ThresholdTypes.Otsu);
        // Background is the majority class; make ink = 1.
        if (OpenCvSharp.Cv2.CountNonZero(binary) > binary.Width * binary.Height / 2)
            OpenCvSharp.Cv2.Subtract(OpenCvSharp.Scalar.All(1), binary, binary);
        using var sums = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.Reduce(binary, sums, OpenCvSharp.ReduceDimension.Row,
            OpenCvSharp.ReduceTypes.Sum, OpenCvSharp.MatType.CV_32S);
        var columns = new int[rect.Width];
        for (int x = 0; x < columns.Length; x++) columns[x] = sums.At<int>(0, x);
        return columns;
    }

    /// <summary>Longest run of ink-free columns inside <paramref name="bounds"/>;
    /// <paramref name="interiorOnly"/> ignores blank margins at either edge.</summary>
    private static int LongestBlankRun(OpenCvSharp.Mat image, Rect bounds, bool interiorOnly = false)
    {
        int x0 = Math.Clamp((int)Math.Floor(bounds.X), 0, image.Width - 1);
        int y0 = Math.Clamp((int)Math.Floor(bounds.Y), 0, image.Height - 1);
        int x1 = Math.Clamp((int)Math.Ceiling(bounds.X + bounds.Width), x0 + 1, image.Width);
        int y1 = Math.Clamp((int)Math.Ceiling(bounds.Y + bounds.Height), y0 + 1, image.Height);
        try
        {
            var columns = InkPerColumn(image, new OpenCvSharp.Rect(x0, y0, x1 - x0, y1 - y0));
            int maxInk = Math.Max(0, (int)Math.Round((y1 - y0) * 0.03));
            int start = 0, end = columns.Length;
            if (interiorOnly)
            {
                while (start < end && columns[start] <= maxInk) start++;
                while (end > start && columns[end - 1] <= maxInk) end--;
            }
            int longest = 0, current = 0;
            for (int x = start; x < end; x++)
            {
                current = columns[x] <= maxInk ? current + 1 : 0;
                longest = Math.Max(longest, current);
            }
            return longest;
        }
        catch
        {
            return 0;
        }
    }

    private static string[] SplitTextByWeights(
        string text,
        IReadOnlyList<int> weights)
    {
        if (weights.Count == 0)
            return Array.Empty<string>();

        if (weights.Count == 1)
            return [text];

        var result = new string[weights.Count];
        int totalWeight = Math.Max(1, weights.Sum());
        int offset = 0;
        int accumulated = 0;

        for (int i = 0; i < weights.Count; i++)
        {
            int end;
            if (i == weights.Count - 1)
            {
                end = text.Length;
            }
            else
            {
                accumulated += weights[i];
                int target = (int)Math.Round(
                    text.Length * accumulated / (double)totalWeight);
                end = Math.Clamp(
                    target,
                    offset,
                    text.Length);
            }

            result[i] = end > offset
                ? text[offset..end]
                : string.Empty;
            offset = end;
        }

        return result;
    }

    private static bool IsUnsupportedSingleGlyph(
        string text,
        Rect bounds,
        IReadOnlyList<Rect> detectorBounds,
        IReadOnlyList<HintLine> lines,
        int wordIndex)
    {
        int alphanumeric = text.Count(char.IsLetterOrDigit);
        if (alphanumeric != 1)
            return false;

        bool detectorSupport = detectorBounds.Any(detector =>
            OverlapOverSmaller(bounds, detector) >= 0.25);

        if (detectorSupport)
            return false;

        bool lineHasOtherWords = lines.Any(line =>
            line.WordIndices.Contains(wordIndex) &&
            line.WordIndices.Length > 1);

        return lineHasOtherWords;
    }

    private static IReadOnlyList<OcrWord> MergeAdjacentFragments(
        OpenCvSharp.Mat image,
        IReadOnlyList<OcrWord> words)
    {
        if (words.Count < 2)
            return words;

        // Reading order by line (not raw top edge), so same-line neighbours of different heights are adjacent.
        var nonEmpty = words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
        var sorted = OcrTextLayout.GroupLines(nonEmpty.Select(w => w.BoundsPixels).ToList())
            .SelectMany(line => line)
            .Select(i => nonEmpty[i])
            .ToList();

        var result = new List<OcrWord>(sorted.Count);

        for (int i = 0; i < sorted.Count; i++)
        {
            var current = sorted[i];

            while (i + 1 < sorted.Count)
            {
                var next = sorted[i + 1];
                var a = current.BoundsPixels;
                var b = next.BoundsPixels;

                if (!SameVisualLine(a, b))
                    break;

                double gap = b.X - (a.X + a.Width);
                double height = Math.Max(a.Height, b.Height);
                string left = NormalizeWhitespace(current.Text);
                string right = NormalizeWhitespace(next.Text);

                bool boundaryPunctuation =
                    left.Length > 0 &&
                    right.Length > 0 &&
                    (!char.IsLetterOrDigit(left[^1]) ||
                     !char.IsLetterOrDigit(right[0]));

                // Split tokens ("1" + ".30", "Ctrl" + "+S") rejoin across a small gap, but
                // punctuation that is normally followed/preceded by a space ("Hello," "world!",
                // "tax" "(incl.", "—") only joins when the glyphs actually touch.
                bool spacedPunctuation =
                    (",;:!?)]}»”".Contains(left[^1]) && char.IsLetterOrDigit(right[0])) ||
                    ("([{«“".Contains(right[0]) && char.IsLetterOrDigit(left[^1])) ||
                    "—–".Contains(left[^1]) || "—–".Contains(right[0]);
                double maxGap = spacedPunctuation
                    ? Math.Max(1.5, height * 0.12)
                    : Math.Max(3, height * 0.45);

                bool punctuationJoin =
                    boundaryPunctuation &&
                    gap >= -height * 0.15 &&
                    gap <= maxGap;

                if (!punctuationJoin)
                    break;

                // Boxes may be detector-padded until they touch; a real word space still shows as
                // blank columns. Checked only where a space is plausible: after "," etc., and around
                // a standalone symbol ("cold / 0.25"). Hyphens and periods look blank to this test.
                bool standaloneSymbol =
                    !left.Any(char.IsLetterOrDigit) || !right.Any(char.IsLetterOrDigit);
                if (spacedPunctuation || standaloneSymbol)
                {
                    double from = a.X + a.Width * 0.5;
                    double to = b.X + b.Width * 0.5;
                    var between = new Rect(from, Math.Min(a.Y, b.Y), Math.Max(1, to - from), height);
                    double space = height * (spacedPunctuation ? 0.15 : 0.22);
                    if (LongestBlankRun(image, between) >= Math.Max(2, space))
                        break;
                }

                if (gap > 1 &&
                    FindStrongVisualSegments(
                        image,
                        UnionBounds(a, b)).Count >= 2)
                {
                    break;
                }

                if (left.Length > 0 &&
                    right.Length > 0 &&
                    left[^1] == right[0] &&
                    !char.IsLetterOrDigit(left[^1]))
                {
                    right = right[1..];
                }

                string mergedText = left + right;
                current = new OcrWord(
                    mergedText,
                    UnionBounds(a, b));
                i++;
            }

            result.Add(current);
        }

        return result;
    }

    private static string AlphanumericCore(string text) =>
        new(text.Where(char.IsLetterOrDigit).ToArray());

    private static IReadOnlyList<OcrWord> Deduplicate(
        IReadOnlyList<OcrWord> words)
    {
        var result = new List<OcrWord>(words.Count);

        foreach (var word in words
                     .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                     .OrderBy(w => w.BoundsPixels.Y)
                     .ThenBy(w => w.BoundsPixels.X))
        {
            bool handled = false;

            for (int i = 0; i < result.Count; i++)
            {
                var existing = result[i];
                double overlap = OverlapOverSmaller(
                    existing.BoundsPixels,
                    word.BoundsPixels);

                if (overlap < 0.65)
                    continue;

                string existingText = NormalizeWhitespace(existing.Text);
                string wordText = NormalizeWhitespace(word.Text);

                if (string.Equals(
                        existingText,
                        wordText,
                        StringComparison.OrdinalIgnoreCase))
                {
                    handled = true;
                    break;
                }

                string existingCore = AlphanumericCore(existingText);
                string wordCore = AlphanumericCore(wordText);

                if (existingCore.Length > 0 &&
                    wordCore.Length > 0 &&
                    (existingCore.Contains(
                         wordCore,
                         StringComparison.OrdinalIgnoreCase) ||
                     wordCore.Contains(
                         existingCore,
                         StringComparison.OrdinalIgnoreCase)))
                {
                    if (wordCore.Length > existingCore.Length)
                        result[i] = word;

                    handled = true;
                    break;
                }
            }

            if (!handled)
                result.Add(word);
        }

        return result;
    }

    private static bool SameVisualLine(Rect a, Rect b)
    {
        double top = Math.Max(a.Y, b.Y);
        double bottom = Math.Min(
            a.Y + a.Height,
            b.Y + b.Height);
        double overlap = bottom - top;
        double minHeight = Math.Min(a.Height, b.Height);

        return minHeight > 0 &&
               overlap / minHeight >= 0.45;
    }

    private static double VerticalOverlapRatio(Rect a, Rect b)
    {
        double top = Math.Max(a.Y, b.Y);
        double bottom = Math.Min(
            a.Y + a.Height,
            b.Y + b.Height);
        double overlap = bottom - top;
        double minHeight = Math.Min(a.Height, b.Height);

        return minHeight > 0
            ? Math.Max(0, overlap) / minHeight
            : 0;
    }

    private static Rect UnionBounds(Rect a, Rect b)
    {
        double x0 = Math.Min(a.X, b.X);
        double y0 = Math.Min(a.Y, b.Y);
        double x1 = Math.Max(a.X + a.Width, b.X + b.Width);
        double y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);

        return new Rect(
            x0,
            y0,
            x1 - x0,
            y1 - y0);
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
                (p.X - detected.ResizeData.PaddingLeft) *
                detected.ResizeData.RatioW,
                0,
                detected.OriginalWidth);
            double y = Math.Clamp(
                (p.Y - detected.ResizeData.PaddingTop) *
                detected.ResizeData.RatioH,
                0,
                detected.OriginalHeight);

            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        return maxX > minX && maxY > minY
            ? new Rect(minX, minY, maxX - minX, maxY - minY)
            : new Rect();
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
        double smaller = Math.Min(
            a.Width * a.Height,
            b.Width * b.Height);

        return smaller > 0 ? intersection / smaller : 0;
    }

    private static int CompactLength(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? 0
            : text.Count(ch => !char.IsWhiteSpace(ch));

    private static string RemoveWhitespace(string text) =>
        new(text.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    private static string NormalizeWhitespace(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join(
                ' ',
                text.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries));

    private static string GetLetterScript(char ch)
    {
        if ((ch >= 'A' && ch <= 'Z') ||
            (ch >= 'a' && ch <= 'z'))
            return "ascii";

        if ((ch >= '\u00C0' && ch <= '\u024F') ||
            (ch >= '\u1E00' && ch <= '\u1EFF'))
            return "latin";

        if (ch >= '\u0400' && ch <= '\u052F')
            return "cyrillic";

        if ((ch >= '\u3040' && ch <= '\u30FF') ||
            (ch >= '\u3400' && ch <= '\u9FFF'))
            return "cjk";

        if ((ch >= '\u1100' && ch <= '\u11FF') ||
            (ch >= '\uAC00' && ch <= '\uD7AF'))
            return "korean";

        if (ch >= '\u0E00' && ch <= '\u0E7F')
            return "th";

        if (ch >= '\u0370' && ch <= '\u03FF')
            return "el";

        if ((ch >= '\u0600' && ch <= '\u06FF') ||
            (ch >= '\u0750' && ch <= '\u077F') ||
            (ch >= '\u08A0' && ch <= '\u08FF'))
            return "arabic";

        if (ch >= '\u0900' && ch <= '\u097F')
            return "devanagari";

        if (ch >= '\u0B80' && ch <= '\u0BFF')
            return "ta";

        if (ch >= '\u0C00' && ch <= '\u0C7F')
            return "te";

        return "neutral";
    }

    private static void EnsureInitialized()
    {
        if (_detector != null && _recognizers != null)
            return;

        IOcrDetector? detector = null;

        try
        {
            var installed = PpOcrV5Service.InstalledRecognizerModels();
            if (installed.Count == 0)
                throw new InvalidOperationException(
                    "No PP-OCRv5 recognizer models installed.");

            detector = new ExecutionProviderCPU(
                CreateConfig(installed[0]))
                .CreateDetector();

            _detector = detector;
            _recognizers =
                new Dictionary<string, (PpOcrV5RecognizerSpec, IOcrRecognizer)>(
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

        var recognizer = new ExecutionProviderCPU(
            CreateConfig(spec))
            .CreateRecognizer();

        _recognizers[spec.Key] = (spec, recognizer);
        return recognizer;
    }

    private static OcrConfig CreateConfig(PpOcrV5RecognizerSpec spec) =>
        new(
            PpOcrV5Service.DetectorPath,
            spec.Path,
            spec.Language,
            OCRVersion.PPOCRV5)
        {
            MaxSideLen = 2400,
        };
}
