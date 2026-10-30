using Windows.Foundation;

namespace Clipsy.Drawing;

internal static class ShapeGeometry
{
    public static Rect Bounds(Point start, Point end, bool proportional)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        if (proportional)
        {
            // Keep the constrained shape inside the pointer's drag bounds.
            // Moving either edge towards the anchor must shrink it, even when
            // the other pointer coordinate has not moved.
            double side = Math.Min(Math.Abs(dx), Math.Abs(dy));
            dx = dx < 0 ? -side : side;
            dy = dy < 0 ? -side : side;
        }
        return new Rect(start.X + Math.Min(0, dx), start.Y + Math.Min(0, dy), Math.Abs(dx), Math.Abs(dy));
    }

    public static Point LineEnd(Point start, Point end, bool snapAngle)
    {
        if (!snapAngle) return end;
        double dx = end.X - start.X, dy = end.Y - start.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        const double step = Math.PI / 12; // 15 degrees
        double angle = Math.Round(Math.Atan2(dy, dx) / step, MidpointRounding.AwayFromZero) * step;
        return new Point(start.X + length * Math.Cos(angle), start.Y + length * Math.Sin(angle));
    }
}
