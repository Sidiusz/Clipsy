using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Clipsy.Drawing;

public enum ToolKind
{
    None,
    Pencil,
    Rectangle,
    Ellipse,
    Line,
    Arrow,
    Text,
}

public sealed class DrawingSettings
{
    private const double MinBrushSize = 1.0;
    private const double MaxBrushSize = 64.0;
    private double _brushSize = 3.0;

    public ToolKind Tool { get; set; } = ToolKind.None;
    public Color Color { get; set; } = Microsoft.UI.Colors.Red;

    /// <summary>FontFamily for the Text tool. Defaults to the bundled Onest font,
    /// falling back to Inter / Segoe UI; switchable via the overlay flyout.</summary>
    public string TextFont { get; set; } = "ms-appx:///Assets/Fonts/Onest-VariableFont_wght.ttf#Onest, Inter, Segoe UI, sans-serif";

    /// <summary>Single size parameter driving all tools (pencil/shape thickness,
    /// text size, preview size derive from it).</summary>
    public double BrushSize
    {
        get => _brushSize;
        set => _brushSize = ClampBrush(value);
    }

    public double PencilThickness
    {
        get => BrushSize;
        set => BrushSize = value;
    }

    public double RectangleThickness
    {
        get => System.Math.Max(1.0, BrushSize * 0.7);
        set => BrushSize = value / 0.7;
    }

    public double EllipseThickness
    {
        get => System.Math.Max(1.0, BrushSize * 0.7);
        set => BrushSize = value / 0.7;
    }

    public double LineThickness
    {
        get => System.Math.Max(1.0, BrushSize * 0.7);
        set => BrushSize = value / 0.7;
    }

    public double TextSize
    {
        get => System.Math.Max(8.0, BrushSize * 6.0);
        set => BrushSize = value / 6.0;
    }

    // Diameter equals the stroke pixel width (small floor so the preview never
    // vanishes for 1-2 px brushes).
    public double PreviewDiameter => System.Math.Max(2.0, BrushSize);

    private static double ClampBrush(double value)
        => System.Math.Clamp(value, MinBrushSize, MaxBrushSize);
}

public abstract class DrawElement
{
    public required UIElement Visual { get; init; }
    public abstract bool HitTest(Point p, double radius);
    public abstract Rect BoundingBox { get; }
}

public sealed class StrokeElement : DrawElement
{
    public required List<Point> Points { get; init; }
    public double Thickness { get; init; }

    public override Rect BoundingBox
    {
        get
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var p in Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
    }

    public override bool HitTest(Point p, double radius)
    {
        var r = radius + Thickness * 0.5;
        for (int i = 1; i < Points.Count; i++)
        {
            if (SegmentDistance(Points[i - 1], Points[i], p) <= r) return true;
        }
        return false;
    }

    private static double SegmentDistance(Point a, Point b, Point p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-6)
        {
            dx = p.X - a.X;
            dy = p.Y - a.Y;
            return System.Math.Sqrt(dx * dx + dy * dy);
        }
        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        double cx = a.X + t * dx, cy = a.Y + t * dy;
        double ex = p.X - cx, ey = p.Y - cy;
        return System.Math.Sqrt(ex * ex + ey * ey);
    }
}

public sealed class RectangleElement : DrawElement
{
    public required Rect Bounds { get; init; }
    public double Thickness { get; init; }

    public override Rect BoundingBox => Bounds;

    public override bool HitTest(Point p, double radius)
    {
        var b = Bounds;
        bool insideOuter = p.X >= b.X - radius && p.X <= b.X + b.Width + radius
                        && p.Y >= b.Y - radius && p.Y <= b.Y + b.Height + radius;
        bool insideInner = p.X >= b.X + radius + Thickness && p.X <= b.X + b.Width - radius - Thickness
                        && p.Y >= b.Y + radius + Thickness && p.Y <= b.Y + b.Height - radius - Thickness;
        return insideOuter && !insideInner;
    }
}

public sealed class EllipseElement : DrawElement
{
    public required Rect Bounds { get; init; }
    public double Thickness { get; init; }

    public override Rect BoundingBox => Bounds;

    public override bool HitTest(Point p, double radius)
    {
        var b = Bounds;
        double cx = b.X + b.Width * 0.5;
        double cy = b.Y + b.Height * 0.5;
        double rx = b.Width * 0.5;
        double ry = b.Height * 0.5;
        if (rx <= 0.0 || ry <= 0.0) return false;

        double nx = (p.X - cx) / rx;
        double ny = (p.Y - cy) / ry;
        double outer = nx * nx + ny * ny;
        double shrinkX = System.Math.Max(1.0, rx - (radius + Thickness));
        double shrinkY = System.Math.Max(1.0, ry - (radius + Thickness));
        double inx = (p.X - cx) / shrinkX;
        double iny = (p.Y - cy) / shrinkY;
        double inner = inx * inx + iny * iny;
        return outer <= 1.0 && inner >= 1.0;
    }
}

public sealed class LineElement : DrawElement
{
    public required Point Start { get; init; }
    public required Point End { get; init; }
    public double Thickness { get; init; }

    /// <summary>Arrow tool: draw an open arrowhead at End.</summary>
    public bool EndArrow { get; init; }

    public override Rect BoundingBox
    {
        get
        {
            double minX = System.Math.Min(Start.X, End.X);
            double minY = System.Math.Min(Start.Y, End.Y);
            double maxX = System.Math.Max(Start.X, End.X);
            double maxY = System.Math.Max(Start.Y, End.Y);
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }
    }

    public override bool HitTest(Point p, double radius)
    {
        return SegmentDistance(Start, End, p) <= radius + Thickness * 0.5;
    }

    private static double SegmentDistance(Point a, Point b, Point p)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-6)
        {
            dx = p.X - a.X;
            dy = p.Y - a.Y;
            return System.Math.Sqrt(dx * dx + dy * dy);
        }
        double t = ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq;
        if (t < 0) t = 0; else if (t > 1) t = 1;
        double cx = a.X + t * dx, cy = a.Y + t * dy;
        double ex = p.X - cx, ey = p.Y - cy;
        return System.Math.Sqrt(ex * ex + ey * ey);
    }
}

public sealed class TextElement : DrawElement
{
    public required Point Position { get; set; }
    public required string Text { get; set; }
    public double FontSize { get; init; }
    public Size MeasuredSize { get; set; }

    public override Rect BoundingBox => new(Position.X, Position.Y, MeasuredSize.Width, MeasuredSize.Height);

    public override bool HitTest(Point p, double radius)
    {
        var b = BoundingBox;
        return p.X >= b.X - radius && p.X <= b.X + b.Width + radius
            && p.Y >= b.Y - radius && p.Y <= b.Y + b.Height + radius;
    }
}
