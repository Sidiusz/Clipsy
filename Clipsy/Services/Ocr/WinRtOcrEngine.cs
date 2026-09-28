using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Clipsy.Services;

/// <summary>Default OCR engine (Windows.Media.Ocr): local, languages via FoD,
/// returns word boxes in the bitmap's pixel space.</summary>
public sealed class WinRtOcrEngine : IOcrEngine
{
    public async Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] pngBytes, CancellationToken ct = default)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new OcrUnavailableException("No Windows OCR language is installed for the user profile.");

        using var stream = pngBytes.AsBuffer().AsStream().AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);

        // Windows OCR rejects images larger than MaxImageDimension; scale down and map boxes back.
        uint max = OcrEngine.MaxImageDimension;
        double scale = Math.Min(1.0, Math.Min((double)max / decoder.PixelWidth, (double)max / decoder.PixelHeight));
        var transform = new BitmapTransform();
        if (scale < 1.0)
        {
            transform.ScaledWidth = (uint)Math.Max(1, Math.Floor(decoder.PixelWidth * scale));
            transform.ScaledHeight = (uint)Math.Max(1, Math.Floor(decoder.PixelHeight * scale));
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }
        using var soft = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);

        var result = await engine.RecognizeAsync(soft).AsTask(ct);
        double inv = 1.0 / scale;
        var words = new List<OcrWord>();
        foreach (var line in result.Lines)
        {
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                words.Add(new OcrWord(w.Text, scale < 1.0 ? new Rect(r.X * inv, r.Y * inv, r.Width * inv, r.Height * inv) : r));
            }
        }
        return words;
    }
}
