using Windows.Foundation;

namespace Clipsy.Services;

/// <summary>Moves a region within the actual desktop, including selections spanning adjacent monitors.</summary>
internal static class MonitorSelectionBounds
{
    private const double CoordinateTolerance = 1e-7;
    public static Rect Move(Rect selection, double x, double y, Rect canvas, IReadOnlyList<Rect> monitors)
    {
        double maxX = Math.Max(canvas.X, canvas.Right - selection.Width);
        double maxY = Math.Max(canvas.Y, canvas.Bottom - selection.Height);
        x = Math.Clamp(x, canvas.X, maxX);
        y = Math.Clamp(y, canvas.Y, maxY);
        var desired = new Rect(x, y, selection.Width, selection.Height);
        if (monitors.Count == 0 || IsCovered(desired, monitors)) return desired;

        // Valid placements change at monitor edges and at edges offset by the
        // region size. Project onto those boundaries instead of choosing one
        // monitor, which would prevent a region from straddling the seam.
        var xs = new List<double> { x };
        var ys = new List<double> { y };
        foreach (var m in monitors)
        {
            xs.Add(Math.Clamp(m.X, canvas.X, maxX));
            xs.Add(Math.Clamp(m.Right, canvas.X, maxX));
            xs.Add(Math.Clamp(m.X - selection.Width, canvas.X, maxX));
            xs.Add(Math.Clamp(m.Right - selection.Width, canvas.X, maxX));
            ys.Add(Math.Clamp(m.Y, canvas.Y, maxY));
            ys.Add(Math.Clamp(m.Bottom, canvas.Y, maxY));
            ys.Add(Math.Clamp(m.Y - selection.Height, canvas.Y, maxY));
            ys.Add(Math.Clamp(m.Bottom - selection.Height, canvas.Y, maxY));
        }

        Rect best = desired;
        double bestDistance = double.PositiveInfinity;
        foreach (double cx in xs.Distinct())
        foreach (double cy in ys.Distinct())
        {
            double distance = (cx - x) * (cx - x) + (cy - y) * (cy - y);
            if (distance >= bestDistance) continue;
            var candidate = new Rect(cx, cy, selection.Width, selection.Height);
            if (!IsCovered(candidate, monitors)) continue;
            best = candidate;
            bestDistance = distance;
        }
        // A region larger than the available monitor union cannot fit anywhere.
        // Keep its dimensions and the existing canvas limit in that case.
        return best;
    }

    private static bool IsCovered(Rect region, IReadOnlyList<Rect> monitors)
    {
        foreach (var m in monitors)
            if (region.X >= m.X - CoordinateTolerance && region.Y >= m.Y - CoordinateTolerance
                && region.Right <= m.Right + CoordinateTolerance && region.Bottom <= m.Bottom + CoordinateTolerance)
                return true;

        var remaining = new List<Rect> { region };
        foreach (var m in monitors)
        {
            for (int i = remaining.Count - 1; i >= 0; i--)
            {
                var r = remaining[i];
                double left = Math.Max(r.X, m.X), right = Math.Min(r.Right, m.Right);
                double top = Math.Max(r.Y, m.Y), bottom = Math.Min(r.Bottom, m.Bottom);
                if (right <= left || bottom <= top) continue;
                remaining.RemoveAt(i);
                if (left - r.X > CoordinateTolerance) remaining.Add(new Rect(r.X, r.Y, left - r.X, r.Height));
                if (r.Right - right > CoordinateTolerance) remaining.Add(new Rect(right, r.Y, r.Right - right, r.Height));
                if (top - r.Y > CoordinateTolerance) remaining.Add(new Rect(left, r.Y, right - left, top - r.Y));
                if (r.Bottom - bottom > CoordinateTolerance) remaining.Add(new Rect(left, bottom, right - left, r.Bottom - bottom));
            }
            if (remaining.Count == 0) return true;
        }
        return false;
    }
}
