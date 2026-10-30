using Windows.Foundation;

namespace Clipsy.Services;

/// <summary>Tells a click from a drag, and a real region from a degenerate one.</summary>
internal static class SelectionGesture
{
    public const double MinSize = 4.0;

    public static bool IsDrag(Point start, Point pos)
        => Math.Abs(pos.X - start.X) >= MinSize || Math.Abs(pos.Y - start.Y) >= MinSize;

    public static bool IsUsable(Rect region) => region.Width >= MinSize && region.Height >= MinSize;
}
