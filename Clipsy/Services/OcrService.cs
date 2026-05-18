using System;
using System.Collections.Generic;
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

public static class OcrEngineFactory
{
    public static IOcrEngine Resolve()
    {
        var configured = SettingsService.Instance.Settings.OcrEngine;
        if (string.Equals(configured, "Tesseract", StringComparison.OrdinalIgnoreCase))
        {
            // Tesseract integration is gated on tessdata. Until that ships,
            // fall back to the WinRT engine.
            return new WinRtOcrEngine();
        }
        return new WinRtOcrEngine();
    }
}
