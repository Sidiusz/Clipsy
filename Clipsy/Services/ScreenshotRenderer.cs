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

/// <summary>Rasterizes a selection (cropped frozen pixels + burned-in vector
/// drawing layer) into an encoded byte buffer.</summary>
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
                // System.Drawing.Common can't encode WebP; fall back to PNG.
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
        using var src = ScreenFreezeService.CreateBitmap(frame);

        int px = (int)System.Math.Floor(selectionDip.X * dpiScale);
        int py = (int)System.Math.Floor(selectionDip.Y * dpiScale);
        int pw = System.Math.Max(1, (int)System.Math.Ceiling(selectionDip.Width * dpiScale));
        int ph = System.Math.Max(1, (int)System.Math.Ceiling(selectionDip.Height * dpiScale));
        var srcRect = new System.Drawing.Rectangle(px, py, pw, ph);
        srcRect.Intersect(new System.Drawing.Rectangle(0, 0, src.Width, src.Height));
        if (srcRect.Width == 0 || srcRect.Height == 0)
        {
            return new Bitmap(1, 1);
        }

        var output = new Bitmap(srcRect.Width, srcRect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(output))
        {
            // Use nearest neighbor to avoid interpolation artifacts
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.None;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawImage(src, new System.Drawing.Rectangle(0, 0, srcRect.Width, srcRect.Height),
                srcRect, GraphicsUnit.Pixel);
            BurnDrawings(g, elements, dpiScale, selectionDip.X, selectionDip.Y);
        }
        return output;
    }

    /// <param name="offsetDipX">Subtract from each element's X (root-overlay DIPs)
    /// before the DPI scale, since the output is sized to the cropped selection.</param>
    private static void BurnDrawings(Graphics g, IReadOnlyList<DrawElement> elements,
        double scale, double offsetDipX, double offsetDipY)
    {
        foreach (var el in elements)
        {
            switch (el)
            {
                case StrokeElement s: DrawStroke(g, s, scale, offsetDipX, offsetDipY); break;
                case RectangleElement r: DrawRect(g, r, scale, offsetDipX, offsetDipY); break;
                case EllipseElement ellipse: DrawEllipse(g, ellipse, scale, offsetDipX, offsetDipY); break;
                case LineElement line: DrawLine(g, line, scale, offsetDipX, offsetDipY); break;
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

    private static void DrawEllipse(Graphics g, EllipseElement el, double scale, double ox, double oy)
    {
        var color = ExtractStrokeColor(el);
        using var pen = new Pen(color, (float)(el.Thickness * scale));
        var rect = new RectangleF(
            (float)((el.Bounds.X - ox) * scale),
            (float)((el.Bounds.Y - oy) * scale),
            (float)(el.Bounds.Width * scale),
            (float)(el.Bounds.Height * scale));
        g.DrawEllipse(pen, rect);
    }

    private static void DrawLine(Graphics g, LineElement line, double scale, double ox, double oy)
    {
        var color = ExtractStrokeColor(line);
        using var pen = new Pen(color, (float)(line.Thickness * scale))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round,
        };
        using var arrowCap = line.EndArrow ? new AdjustableArrowCap(3f, 3.5f, false) : null;
        if (arrowCap != null) pen.CustomEndCap = arrowCap;
        var a = new PointF((float)((line.Start.X - ox) * scale), (float)((line.Start.Y - oy) * scale));
        var b = new PointF((float)((line.End.X - ox) * scale), (float)((line.End.Y - oy) * scale));
        g.DrawLine(pen, a, b);
    }

    private static void DrawText(Graphics g, TextElement t, double scale, double ox, double oy)
    {
        var color = ExtractTextColor(t);
        using var brush = new SolidBrush(color);

        var familyName = ExtractTextFamily(t);
        var family = ResolveFontFamily(familyName);
        // FontSize in DIPs; GDI Font wants points (75% of DIPs at 96 DPI).
        var size = (float)(t.FontSize * scale * 0.75);
        Font? font = null;
        try
        {
            font = new Font(family, size, FontStyle.Bold, GraphicsUnit.Point);
        }
        catch
        {
            // Some families only ship a Regular face — Bold throws.
            try { font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Point); }
            catch { font = new Font(FontFamily.GenericSansSerif, size, FontStyle.Bold, GraphicsUnit.Point); }
        }
        var pos = new PointF(
            (float)((t.Position.X - ox) * scale),
            (float)((t.Position.Y - oy) * scale));
        g.DrawString(t.Text, font, brush, pos);
        font.Dispose();
    }

    private static string ExtractTextFamily(TextElement t) => t.FontFamily ?? string.Empty;

    // PrivateFontCollection for bundled .ttf (Onest): GDI ignores ms-appx:// URIs,
    // so register the file once and pull the family by name.
    private static readonly System.Drawing.Text.PrivateFontCollection _privateFonts = new();
    private static FontFamily? _onestFamily;
    private static bool _onestLoaded;

    private static FontFamily ResolveFontFamily(string source)
    {
        // FontFamily.Source may be a plain name, a CSS-style list, or an
        // ms-appx:// URI with a #Family suffix.
        if (string.IsNullOrWhiteSpace(source)) return FontFamily.GenericSansSerif;

        // Walk the fallback chain left→right, return the first family GDI can resolve.
        foreach (var rawPart in source.Split(','))
        {
            var part = rawPart.Trim();
            if (string.IsNullOrEmpty(part)) continue;

            // ms-appx URI: extract postscript family after '#' and load the
            // referenced .ttf into our private collection.
            if (part.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase))
            {
                int hash = part.IndexOf('#');
                if (hash < 0) continue;
                string family = part.Substring(hash + 1).Trim();
                EnsureBundledOnestLoaded();
                if (_onestFamily != null &&
                    string.Equals(_onestFamily.Name, family, StringComparison.OrdinalIgnoreCase))
                    return _onestFamily;
                continue;
            }

            // Strip CSS-style generic ("sans-serif", "serif", "monospace").
            if (part.Equals("sans-serif", StringComparison.OrdinalIgnoreCase)) return FontFamily.GenericSansSerif;
            if (part.Equals("serif", StringComparison.OrdinalIgnoreCase))      return FontFamily.GenericSerif;
            if (part.Equals("monospace", StringComparison.OrdinalIgnoreCase))  return FontFamily.GenericMonospace;

            try { return new FontFamily(part); } catch { /* not installed, try next */ }
        }
        return FontFamily.GenericSansSerif;
    }

    private static void EnsureBundledOnestLoaded()
    {
        if (_onestLoaded) return;
        _onestLoaded = true;
        try
        {
            var dir = AppContext.BaseDirectory;
            var path = Path.Combine(dir, "Assets", "Fonts", "Onest-VariableFont_wght.ttf");
            if (File.Exists(path))
            {
                _privateFonts.AddFontFile(path);
                if (_privateFonts.Families.Length > 0)
                    _onestFamily = _privateFonts.Families[0];
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Onest font load failed: {ex.Message}");
        }
    }

    private static Color ExtractStrokeColor(DrawElement el) => ToGdiColor(el.Color);

    private static Color ExtractTextColor(TextElement t) => ToGdiColor(t.Color);

    private static Color ToGdiColor(WinColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);
}
