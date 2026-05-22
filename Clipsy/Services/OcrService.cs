using System;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>
/// Default OCR engine — Windows.Media.Ocr. Local, no network, supports
/// languages installed via Windows feature-on-demand. Returns word
/// bounding boxes in the input bitmap's pixel coordinate space.
/// </summary>
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

/// <summary>
/// Tesseract OCR engine — user-downloaded tessdata via TessdataService.
/// Falls back silently if no language files are installed.
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine
{
    public Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] pngBytes)
    {
        return Task.Run<IReadOnlyList<OcrWord>>(() =>
        {
            var words = new List<OcrWord>();
            try
            {
                var langs = TessdataService.InstalledSelectedCodes();
                if (langs.Count == 0)
                    throw new InvalidOperationException("No Tesseract language files installed.");

                var langStr = string.Join("+", langs);
                using var engine = new Tesseract.TesseractEngine(TessdataService.StorageDir, langStr, Tesseract.EngineMode.Default);
                using var pix = Tesseract.Pix.LoadFromMemory(pngBytes);
                using var page = engine.Process(pix);
                using var iter = page.GetIterator();
                iter.Begin();
                do
                {
                    if (iter.TryGetBoundingBox(Tesseract.PageIteratorLevel.Word, out var r))
                    {
                        var text = iter.GetText(Tesseract.PageIteratorLevel.Word)?.Trim();
                        if (!string.IsNullOrEmpty(text))
                            words.Add(new OcrWord(text, new Rect(r.X1, r.Y1, r.X2 - r.X1, r.Y2 - r.Y1)));
                    }
                }
                while (iter.Next(Tesseract.PageIteratorLevel.Word));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Clipsy] Tesseract failed: {ex.Message}");
            }
            return words;
        });
    }
}

public static class OcrEngineFactory
{
    public static IOcrEngine Resolve()
    {
        var configured = SettingsService.Instance.Settings.OcrEngine;
        if (string.Equals(configured, "Tesseract", StringComparison.OrdinalIgnoreCase)
            && TessdataService.InstalledSelectedCodes().Count > 0)
        {
            return new TesseractOcrEngine();
        }
        return new WinRtOcrEngine();
    }
}
