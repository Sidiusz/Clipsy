using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using Clipsy.Drawing;
using Windows.Foundation;
using WinColor = Windows.UI.Color;

namespace Clipsy.Services;

/// <summary>
/// Rasterizes a selection — frozen pixels cropped from the captured PNG plus
/// the burned-in vector drawing layer — into a PNG byte buffer.
/// </summary>
public static class ScreenshotRenderer
{
    public enum OutputFormat { Png, Jpeg, Webp }

    public static OutputFormat ParseFormat(string s)
    {
        return s?.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => OutputFormat.Jpeg,
            "webp" => OutputFormat.Webp,
            _ => OutputFormat.Png,
        };
    }

    public static string ExtensionFor(OutputFormat fmt) => fmt switch
    {
        OutputFormat.Jpeg => ".jpg",
        OutputFormat.Webp => ".webp",
        _ => ".png",
    };

    /// <param name="selectionDip">Selection rect in overlay-window DIPs.</param>
    /// <param name="dpiScale">Scale factor that converts DIPs to source-bitmap pixels.</param>
    public static byte[] RenderPng(
        ScreenFreezeService.FrozenFrame frame,
        Rect selectionDip,
        IReadOnlyList<DrawElement> elements,
        double dpiScale)
    {
        return RenderEncoded(frame, selectionDip, elements, dpiScale, OutputFormat.Png, 95);
    }

    public static byte[] RenderEncoded(
        ScreenFreezeService.FrozenFrame frame,
        Rect selectionDip,
        IReadOnlyList<DrawElement> elements,
        double dpiScale,
        OutputFormat format,
        int quality = 90)
    {
        using var bmp = RenderBitmap(frame, selectionDip, elements, dpiScale);
        using var ms = new MemoryStream();
        switch (format)
        {
            case OutputFormat.Jpeg:
                {
                    var encoder = GetEncoder(ImageFormat.Jpeg);
                    if (encoder == null)
                    {
                        bmp.Save(ms, ImageFormat.Jpeg);
                    }
                    else
                    {
                        using var ep = new EncoderParameters(1);
                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,
                            System.Math.Clamp((long)quality, 1L, 100L));
                        // JPG has no alpha channel - drop alpha first to avoid pink-tinted output.
                        using var flat = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb);
                        using (var g = Graphics.FromImage(flat))
                        {
                            g.Clear(Color.White);
                            g.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
                        }
                        flat.Save(ms, encoder, ep);
                    }
                    break;
                }
            case OutputFormat.Webp:
                // System.Drawing.Common does not encode WebP. Fall back to PNG so
                // the user still gets a valid file. A real WebP encoder is a
                // follow-up; for now we log and return PNG bytes.
                System.Diagnostics.Debug.WriteLine("[Clipsy] WebP requested but not supported; saving PNG.");
                bmp.Save(ms, ImageFormat.Png);
                break;
            default:
                bmp.Save(ms, ImageFormat.Png);
                break;
        }
        return ms.ToArray();
    }

    private static ImageCodecInfo? GetEncoder(ImageFormat format)
    {
        foreach (var c in ImageCodecInfo.GetImageEncoders())
        {
            if (c.FormatID == format.Guid) return c;
        }
        return null;
    }

    public static Bitmap RenderBitmap(
        ScreenFreezeService.FrozenFrame frame,
        Rect selectionDip,
        IReadOnlyList<DrawElement> elements,
        double dpiScale)
    {
        using var srcMs = new MemoryStream(frame.PngBytes);
        using var src = new Bitmap(srcMs);

        int px = (int)System.Math.Round(selectionDip.X * dpiScale);
        int py = (int)System.Math.Round(selectionDip.Y * dpiScale);
        int pw = System.Math.Max(1, (int)System.Math.Round(selectionDip.Width * dpiScale));
        int ph = System.Math.Max(1, (int)System.Math.Round(selectionDip.Height * dpiScale));
        var srcRect = new System.Drawing.Rectangle(px, py, pw, ph);
        srcRect.Intersect(new System.Drawing.Rectangle(0, 0, src.Width, src.Height));
        if (srcRect.Width == 0 || srcRect.Height == 0)
        {
            return new Bitmap(1, 1);
        }

        var output = new Bitmap(srcRect.Width, srcRect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(output))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawImage(src, new System.Drawing.Rectangle(0, 0, srcRect.Width, srcRect.Height),
                srcRect, GraphicsUnit.Pixel);
            BurnDrawings(g, elements, dpiScale, selectionDip.X, selectionDip.Y);
        }
        return output;
    }

    /// <param name="offsetDipX">Subtract this from each element's X before
    /// applying the DPI scale. Element coordinates are stored in root-overlay
    /// DIPs; the output bitmap is sized to the cropped selection.</param>
    private static void BurnDrawings(Graphics g, IReadOnlyList<DrawElement> elements,
        double scale, double offsetDipX, double offsetDipY)
    {
        foreach (var el in elements)
        {
            switch (el)
            {
                case StrokeElement s: DrawStroke(g, s, scale, offsetDipX, offsetDipY); break;
                case RectangleElement r: DrawRect(g, r, scale, offsetDipX, offsetDipY); break;
                case TextElement t: DrawText(g, t, scale, offsetDipX, offsetDipY); break;
            }
        }
    }

    private static void DrawStroke(Graphics g, StrokeElement s, double scale, double ox, double oy)
    {
        if (s.Points.Count < 2) return;
        var color = ExtractStrokeColor(s);
        using var pen = new Pen(color, (float)(s.Thickness * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        var pts = new PointF[s.Points.Count];
        for (int i = 0; i < s.Points.Count; i++)
        {
            pts[i] = new PointF(
                (float)((s.Points[i].X - ox) * scale),
                (float)((s.Points[i].Y - oy) * scale));
        }
        g.DrawLines(pen, pts);
    }

    private static void DrawRect(Graphics g, RectangleElement r, double scale, double ox, double oy)
    {
        var color = ExtractStrokeColor(r);
        using var pen = new Pen(color, (float)(r.Thickness * scale));
        var rect = new RectangleF(
            (float)((r.Bounds.X - ox) * scale),
            (float)((r.Bounds.Y - oy) * scale),
            (float)(r.Bounds.Width * scale),
            (float)(r.Bounds.Height * scale));
        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
    }

    private static void DrawText(Graphics g, TextElement t, double scale, double ox, double oy)
    {
        var color = ExtractTextColor(t);
        using var brush = new SolidBrush(color);
        using var font = new Font("Segoe UI", (float)(t.FontSize * scale * 0.75), FontStyle.Bold, GraphicsUnit.Point);
        var pos = new PointF(
            (float)((t.Position.X - ox) * scale),
            (float)((t.Position.Y - oy) * scale));
        g.DrawString(t.Text, font, brush, pos);
    }

    private static Color ExtractStrokeColor(DrawElement el)
    {
        if (el.Visual is Microsoft.UI.Xaml.Shapes.Shape shape
            && shape.Stroke is Microsoft.UI.Xaml.Media.SolidColorBrush sb)
        {
            return ToGdiColor(sb.Color);
        }
        return Color.Red;
    }

    private static Color ExtractTextColor(TextElement t)
    {
        if (t.Visual is Microsoft.UI.Xaml.Controls.TextBlock tb
            && tb.Foreground is Microsoft.UI.Xaml.Media.SolidColorBrush sb)
        {
            return ToGdiColor(sb.Color);
        }
        return Color.Red;
    }

    private static Color ToGdiColor(WinColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
}
