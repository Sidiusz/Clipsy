using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
        // Prefer Tesseract when installed; WinRT OCR uses one profile language.
        if (TessdataService.InstalledSelectedCodes().Count > 0)
            return new TesseractOcrEngine();
        return new WinRtOcrEngine();
    }
}
