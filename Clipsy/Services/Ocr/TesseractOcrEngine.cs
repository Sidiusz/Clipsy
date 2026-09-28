using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Rectangle = System.Drawing.Rectangle;

namespace Clipsy.Services;

/// <summary>Tesseract OCR engine (user-downloaded tessdata via TessdataService).</summary>
public sealed class TesseractOcrEngine : IOcrEngine
{
    // TesseractEngine loads its model on construction (hundreds of ms) and isn't thread-safe:
    // keep one per language and serialize use.
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Tesseract.TesseractEngine> Engines = new();

    public static void Reset()
    {
        lock (Sync)
        {
            foreach (var e in Engines.Values)
            {
                try { e.Dispose(); } catch { }
            }
            Engines.Clear();
        }
    }

    public Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] pngBytes, CancellationToken ct = default)
    {
        return Task.Run<IReadOnlyList<OcrWord>>(() =>
        {
            var langs = TessdataService.InstalledSelectedCodes();
            if (langs.Count == 0)
                throw new OcrUnavailableException("No Tesseract language files installed.");

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
                Exception? lastError = null;
                lock (Sync)
                {
                    foreach (var lang in langs)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var (w, conf) = RunSingle(lang, pix, scaleUp);
                            if (conf > bestConf) { bestConf = conf; best = w; }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            lastError = ex;
                            Diagnostics.Log($"Tesseract lang '{lang}' failed", ex);
                            if (Engines.Remove(lang, out var broken)) broken.Dispose();
                        }
                    }
                }
                if (best == null && lastError != null) throw lastError;
                return best ?? new List<OcrWord>();
            }
            finally
            {
                if (scaled) pix.Dispose();
            }
        }, ct);
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

    // Caller holds Sync.
    private static (List<OcrWord> words, float confidence) RunSingle(string lang, Tesseract.Pix pix, float scaleUp)
    {
        if (!Engines.TryGetValue(lang, out var engine))
        {
            engine = new Tesseract.TesseractEngine(TessdataService.StorageDir, lang, Tesseract.EngineMode.Default);
            Engines[lang] = engine;
        }

        var words = new List<OcrWord>();
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
}
