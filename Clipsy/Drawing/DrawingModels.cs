using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace Clipsy.Drawing;

public enum ToolKind
{
    None,
    Pencil,
    Rectangle,
    Text,
}

public sealed class DrawingSettings
{
    public ToolKind Tool { get; set; } = ToolKind.None;
    public Color Color { get; set; } = Microsoft.UI.Colors.Red;
    public double PencilThickness { get; set; } = 3.0;
    public double RectangleThickness { get; set; } = 2.0;
    public double TextSize { get; set; } = 18.0;
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
        if (lenSq < 1e-6) { dx = p.X - a.X; dy = p.Y - a.Y; return System.Math.Sqrt(dx * dx + dy * dy); }
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

public sealed class TextElement : DrawElement
{
    public required Point Position { get; init; }
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
