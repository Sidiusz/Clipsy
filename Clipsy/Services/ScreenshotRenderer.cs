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
    /// <param name="selectionDip">Selection rect in overlay-window DIPs.</param>
    /// <param name="dpiScale">Scale factor that converts DIPs to source-bitmap pixels.</param>
    public static byte[] RenderPng(
        ScreenFreezeService.FrozenFrame frame,
        Rect selectionDip,
        IReadOnlyList<DrawElement> elements,
        double dpiScale)
    {
        using var bmp = RenderBitmap(frame, selectionDip, elements, dpiScale);
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
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
            BurnDrawings(g, elements, dpiScale);
        }
        return output;
    }

    private static void BurnDrawings(Graphics g, IReadOnlyList<DrawElement> elements, double scale)
    {
        foreach (var el in elements)
        {
            switch (el)
            {
                case StrokeElement s: DrawStroke(g, s, scale); break;
                case RectangleElement r: DrawRect(g, r, scale); break;
                case TextElement t: DrawText(g, t, scale); break;
            }
        }
    }

    private static void DrawStroke(Graphics g, StrokeElement s, double scale)
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
            pts[i] = new PointF((float)(s.Points[i].X * scale), (float)(s.Points[i].Y * scale));
        }
        g.DrawLines(pen, pts);
    }

    private static void DrawRect(Graphics g, RectangleElement r, double scale)
    {
        var color = ExtractStrokeColor(r);
        using var pen = new Pen(color, (float)(r.Thickness * scale));
        var rect = new RectangleF(
            (float)(r.Bounds.X * scale),
            (float)(r.Bounds.Y * scale),
            (float)(r.Bounds.Width * scale),
            (float)(r.Bounds.Height * scale));
        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
    }

    private static void DrawText(Graphics g, TextElement t, double scale)
    {
        var color = ExtractTextColor(t);
        using var brush = new SolidBrush(color);
        using var font = new Font("Segoe UI", (float)(t.FontSize * scale * 0.75), FontStyle.Bold, GraphicsUnit.Point);
        var pos = new PointF((float)(t.Position.X * scale), (float)(t.Position.Y * scale));
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
