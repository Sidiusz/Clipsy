using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace Clipsy.Drawing;

// Committed elements render on a GPU Win2D canvas, redrawn only on change.
public sealed class DrawingController
{
    private CanvasControl? _canvas;
    private readonly List<DrawElement> _elements = new();
    private readonly Stack<HistoryOp> _undo = new();
    private readonly Stack<HistoryOp> _redo = new();
    private DrawElement[]? _editBefore;

    private static readonly CanvasStrokeStyle RoundStroke = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round,
    };

    // Baked committed content; an active-draw frame is one blit.
    private CanvasRenderTarget? _cache;
    private bool _cacheDirty = true;

    // Active stroke painted segment-by-segment into the cache (O(1) per move).
    private bool _activeOpen;
    private bool _activeMissedPaint;
    private Color _activeColor;
    private double _activeThickness;
    private Point _activeLast;

    // Move tool: selected element is excluded from the cache and drawn on top.
    private DrawElement? _selected;
    private static readonly CanvasStrokeStyle DashStroke = new() { DashStyle = CanvasDashStyle.Dash };

    public DrawingSettings Settings { get; } = new();
    public IReadOnlyList<DrawElement> Elements => _elements;

    public DrawingController() { }

    public void AttachCanvas(CanvasControl canvas)
    {
        if (ReferenceEquals(_canvas, canvas))
            return;

        if (_canvas != null)
            _canvas.Draw -= OnDraw;

        _canvas = canvas;
        _canvas.Draw += OnDraw;
        _cacheDirty = true;
        _canvas?.Invalidate();
    }

    public void DetachCanvas()
    {
        if (_canvas != null)
            _canvas.Draw -= OnDraw;

        _canvas = null;
        DisposeResources();
    }

    // Committed content changed (move/undo/etc.): rebuild the cache next draw.
    public void InvalidateCommitted()
    {
        _cacheDirty = true;
        _canvas?.Invalidate();
    }

    public void BeginActiveStroke(Color color, double thickness, Point start)
    {
        _activeColor = color;
        _activeThickness = thickness;
        _activeLast = start;
        _activeOpen = true;
        _activeMissedPaint = false;
        PaintActiveSegment(start, start);
    }

    // Already-painted segments keep the old width; rebuild from the element at the end.
    public void SetActiveThickness(double thickness)
    {
        _activeThickness = thickness;
        _activeMissedPaint = true;
    }

    public void AppendActiveStroke(Point pt)
    {
        if (!_activeOpen) return;
        PaintActiveSegment(_activeLast, pt);
        _activeLast = pt;
    }

    public void AppendActiveStrokeBatch(IReadOnlyList<Point> points)
    {
        if (!_activeOpen || points.Count == 0) return;
        if (_cache == null)
        {
            _activeMissedPaint = true;
            _activeLast = points[^1];
            _canvas?.Invalidate();
            return;
        }
        using var ds = _cache.CreateDrawingSession();
        var last = _activeLast;
        foreach (var pt in points)
        {
            ds.DrawLine(V(last), V(pt), _activeColor, (float)_activeThickness, RoundStroke);
            last = pt;
        }
        _activeLast = last;
        _canvas?.Invalidate();
    }

    // Pixels are already in the cache; add the element without a rebuild.
    public void EndActiveStroke(StrokeElement e)
    {
        _activeOpen = false;
        var before = _elements.ToArray();
        _elements.Add(e);
        PushListChange(before);
        if (_activeMissedPaint) InvalidateCommitted();
    }

    // Discard an in-progress stroke: repaint the cache from committed elements.
    public void CancelActiveStroke()
    {
        if (!_activeOpen) return;
        _activeOpen = false;
        InvalidateCommitted();
    }

    // ---------- Move tool ----------

    public DrawElement? Selected => _selected;

    public void SetSelected(DrawElement? e) { _selected = e; InvalidateCommitted(); }

    public void MoveSelected(double dx, double dy)
    {
        if (_selected == null) return;
        _selected.Offset(dx, dy);
        _canvas?.Invalidate();
    }

    // Topmost-first, for click-cycle selection.
    public List<DrawElement> HitTestAll(Point p, double radius)
    {
        var list = new List<DrawElement>();
        for (int i = _elements.Count - 1; i >= 0; i--)
            if (_elements[i].HitTest(p, radius)) list.Add(_elements[i]);
        return list;
    }

    private void PaintActiveSegment(Point a, Point b)
    {
        if (_cache == null) { _activeMissedPaint = true; _canvas?.Invalidate(); return; }
        using var cds = _cache.CreateDrawingSession();
        cds.DrawLine(V(a), V(b), _activeColor, (float)_activeThickness, RoundStroke);
        _canvas?.Invalidate();
    }

    public void DisposeResources()
    {
        _cache?.Dispose();
        _cache = null;
    }

    public void Add(DrawElement e)
    {
        var before = _elements.ToArray();
        _elements.Add(e);
        PushListChange(before);
        InvalidateCommitted();
    }

    /// <summary>Groups the changes of one gesture (e.g. an erase drag) into a single undo step.</summary>
    public void BeginEdit() => _editBefore ??= _elements.ToArray();

    public void EndEdit()
    {
        var before = _editBefore;
        _editBefore = null;
        if (before != null && !before.SequenceEqual(_elements)) PushListChange(before);
    }

    public void RecordMove(DrawElement e, double dx, double dy)
    {
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01) return;
        _undo.Push(new MoveOp(e, dx, dy));
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var op = _undo.Pop();
        switch (op)
        {
            case ListOp l: SetElements(l.Before); break;
            case MoveOp m: m.Element.Offset(-m.Dx, -m.Dy); break;
        }
        _redo.Push(op);
        InvalidateCommitted();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var op = _redo.Pop();
        switch (op)
        {
            case ListOp l: SetElements(l.After); break;
            case MoveOp m: m.Element.Offset(m.Dx, m.Dy); break;
        }
        _undo.Push(op);
        InvalidateCommitted();
        return true;
    }

    /// <summary>Removes every element as one undoable step.</summary>
    public bool ClearDrawings()
    {
        if (_elements.Count == 0) return false;
        var before = _elements.ToArray();
        _elements.Clear();
        _selected = null;
        PushListChange(before);
        InvalidateCommitted();
        return true;
    }

    /// <summary>Resets elements and history (new capture session).</summary>
    public void ClearAll()
    {
        _elements.Clear();
        _undo.Clear();
        _redo.Clear();
        _editBefore = null;
        _selected = null;
        InvalidateCommitted();
    }

    public bool WholeStrokeErase(Point cursor, double radius)
    {
        var before = _editBefore == null ? _elements.ToArray() : null;
        bool changed = false;
        for (int i = _elements.Count - 1; i >= 0; i--)
        {
            if (!_elements[i].HitTest(cursor, radius)) continue;
            _elements.RemoveAt(i);
            changed = true;
        }
        if (changed) CommitErase(before);
        return changed;
    }

    // Pencil strokes split around the eraser; shapes/text removed whole on touch.
    public bool PartialErase(Point cursor, double radius)
    {
        var before = _editBefore == null ? _elements.ToArray() : null;
        bool changed = false;
        for (int i = _elements.Count - 1; i >= 0; i--)
        {
            var el = _elements[i];
            switch (el)
            {
                case StrokeElement s:
                    var splits = SplitStrokeAroundEraser(s, cursor, radius);
                    if (splits == null) continue;
                    changed = true;
                    _elements.RemoveAt(i);
                    foreach (var sub in splits)
                        _elements.Insert(i, sub);
                    break;
                default:
                    if (el.HitTest(cursor, radius))
                    {
                        _elements.RemoveAt(i);
                        changed = true;
                    }
                    break;
            }
        }
        if (changed) CommitErase(before);
        return changed;
    }

    private void CommitErase(DrawElement[]? standaloneBefore)
    {
        if (_selected != null && !_elements.Contains(_selected)) _selected = null;
        if (standaloneBefore != null) PushListChange(standaloneBefore);
        InvalidateCommitted();
    }

    private static List<StrokeElement>? SplitStrokeAroundEraser(StrokeElement stroke, Point cursor, double radius)
    {
        double reach = radius + stroke.Thickness * 0.5;
        var src = stroke.Points;
        if (src.Count == 0) return null;
        bool anyHit = DistanceSq(src[0], cursor) <= reach * reach;
        for (int i = 1; i < src.Count && !anyHit; i++)
            anyHit = SegmentDistanceSq(src[i - 1], src[i], cursor) <= reach * reach;
        if (!anyHit) return null;

        // Densify long segments so the eraser can cut through their middle.
        double step = Math.Max(1.0, reach * 0.5);
        var pts = new List<Point>(src.Count * 2) { src[0] };
        for (int i = 1; i < src.Count; i++)
        {
            var a = src[i - 1];
            var b = src[i];
            double len = Math.Sqrt(DistanceSq(a, b));
            int n = (int)(len / step);
            for (int k = 1; k < n; k++)
            {
                double t = k / (double)n;
                pts.Add(new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
            }
            pts.Add(b);
        }

        var results = new List<StrokeElement>();
        var run = new List<Point>();
        foreach (var p in pts)
        {
            if (DistanceSq(p, cursor) > reach * reach)
            {
                run.Add(p);
                continue;
            }
            if (run.Count >= 2) results.Add(CloneStrokeWithPoints(stroke, run));
            run = new List<Point>();
        }
        if (run.Count >= 2) results.Add(CloneStrokeWithPoints(stroke, run));
        return results;
    }

    private static double DistanceSq(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    private static double SegmentDistanceSq(Point a, Point b, Point p)
    {
        double vx = b.X - a.X, vy = b.Y - a.Y;
        double len2 = vx * vx + vy * vy;
        double t = len2 < 1e-9 ? 0 : Math.Clamp(((p.X - a.X) * vx + (p.Y - a.Y) * vy) / len2, 0, 1);
        return DistanceSq(new Point(a.X + vx * t, a.Y + vy * t), p);
    }

    private static StrokeElement CloneStrokeWithPoints(StrokeElement orig, List<Point> pts)
        => new()
        {
            Points = pts,
            Thickness = orig.Thickness,
            Color = orig.Color,
        };

    // ---------- GPU rendering ----------

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        EnsureCache(sender);
        if (_cache != null) ds.DrawImage(_cache);
        if (_selected != null)
        {
            try { DrawOne(ds, _selected); } catch { }
            DrawSelectionOutline(ds, _selected.BoundingBox);
        }
    }

    private static void DrawSelectionOutline(CanvasDrawingSession ds, Rect b)
    {
        const float pad = 4f;
        var accent = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xC1, 0x07);
        ds.DrawRectangle(
            (float)(b.X - pad), (float)(b.Y - pad),
            (float)(b.Width + pad * 2), (float)(b.Height + pad * 2),
            accent, 1.5f, DashStroke);
    }

    private void EnsureCache(CanvasControl sender)
    {
        float w = (float)sender.Size.Width;
        float h = (float)sender.Size.Height;
        if (w <= 0 || h <= 0) { _cache = null; return; }

        if (_cache == null ||
            System.Math.Abs(_cache.Size.Width - w) > 0.5 ||
            System.Math.Abs(_cache.Size.Height - h) > 0.5)
        {
            _cache?.Dispose();
            _cache = new CanvasRenderTarget(sender, w, h);
            _cacheDirty = true;
        }
        if (!_cacheDirty) return;
        _cacheDirty = false;

        using var cds = _cache.CreateDrawingSession();
        cds.Clear(Microsoft.UI.Colors.Transparent);
        foreach (var el in _elements)
        {
            if (ReferenceEquals(el, _selected)) continue;
            try { DrawOne(cds, el); } catch { } // a bad font must not blank the cache
        }
    }

    /// <summary>Single source of truth for element rendering: the overlay and saved files both use it.</summary>
    internal static void Render(CanvasDrawingSession ds, DrawElement el) => DrawOne(ds, el);

    private static void DrawOne(CanvasDrawingSession ds, DrawElement el)
    {
        switch (el)
        {
            case StrokeElement s: DrawStroke(ds, s); break;
            case RectangleElement r:
                ds.DrawRectangle((float)r.Bounds.X, (float)r.Bounds.Y,
                    (float)r.Bounds.Width, (float)r.Bounds.Height, r.Color, (float)r.Thickness);
                break;
            case EllipseElement e:
                ds.DrawEllipse(
                    (float)(e.Bounds.X + e.Bounds.Width * 0.5),
                    (float)(e.Bounds.Y + e.Bounds.Height * 0.5),
                    (float)(e.Bounds.Width * 0.5), (float)(e.Bounds.Height * 0.5),
                    e.Color, (float)e.Thickness);
                break;
            case LineElement line: DrawLine(ds, line); break;
            case TextElement t: DrawText(ds, t); break;
        }
    }

    private static void DrawStroke(CanvasDrawingSession ds, StrokeElement s)
    {
        if (s.Points.Count < 2) return;
        using var pb = new CanvasPathBuilder(ds);
        pb.BeginFigure(V(s.Points[0]));
        for (int i = 1; i < s.Points.Count; i++) pb.AddLine(V(s.Points[i]));
        pb.EndFigure(CanvasFigureLoop.Open);
        using var geo = CanvasGeometry.CreatePath(pb);
        ds.DrawGeometry(geo, s.Color, (float)s.Thickness, RoundStroke);
    }

    private static void DrawLine(CanvasDrawingSession ds, LineElement line)
    {
        ds.DrawLine(V(line.Start), V(line.End), line.Color, (float)line.Thickness, RoundStroke);
        if (!line.EndArrow) return;
        var (h1, h2) = ArrowHead(line.Start, line.End, line.Thickness);
        ds.DrawLine(V(h1), V(line.End), line.Color, (float)line.Thickness, RoundStroke);
        ds.DrawLine(V(h2), V(line.End), line.Color, (float)line.Thickness, RoundStroke);
    }

    private static void DrawText(CanvasDrawingSession ds, TextElement t)
    {
        DrawTextWithFamily(ds, t, Win2DFontFamily(t.FontFamily));
    }

    private static void DrawTextWithFamily(CanvasDrawingSession ds, TextElement t, string family)
    {
        try
        {
            using var fmt = new CanvasTextFormat
            {
                FontFamily = family,
                FontSize = (float)t.FontSize,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                WordWrapping = CanvasWordWrapping.NoWrap,
            };
            ds.DrawText(t.Text, V(t.Position), t.Color, fmt);
        }
        catch when (family != "Segoe UI")
        {
            DrawTextWithFamily(ds, t, "Segoe UI"); // never let text vanish
        }
    }

    // Win2D needs a family or "path#family"; unpackaged, it can't use ms-appx URIs.
    private static string Win2DFontFamily(string source)
    {
        if (!string.IsNullOrEmpty(source) && source.Contains("Onest", StringComparison.OrdinalIgnoreCase))
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "Onest-VariableFont_wght.ttf");
            if (System.IO.File.Exists(path)) return path + "#Onest";
        }
        if (string.IsNullOrWhiteSpace(source)) return "Segoe UI";
        var first = source.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(first)
            || first.StartsWith("ms-appx", StringComparison.OrdinalIgnoreCase)
            || first.Equals("sans-serif", StringComparison.OrdinalIgnoreCase))
            return "Segoe UI";
        return first;
    }

    // Head scales with the brush but never outgrows the shaft; ~26° per side.
    internal static (Point, Point) ArrowHead(Point a, Point b, double thickness)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3) return (b, b);
        double ux = dx / len, uy = dy / len;
        double headLen = Math.Min(Math.Max(9.0, thickness * 3.0), len);
        const double spread = 0.46;
        double cos = Math.Cos(spread), sin = Math.Sin(spread);
        double bx = -ux, by = -uy;
        var p1 = new Point(b.X + headLen * (bx * cos - by * sin), b.Y + headLen * (bx * sin + by * cos));
        var p2 = new Point(b.X + headLen * (bx * cos + by * sin), b.Y + headLen * (-bx * sin + by * cos));
        return (p1, p2);
    }

    private static Vector2 V(Point p) => new((float)p.X, (float)p.Y);

    private void PushListChange(DrawElement[] before)
    {
        _undo.Push(new ListOp(before, _elements.ToArray()));
        _redo.Clear();
    }

    private void SetElements(DrawElement[] elements)
    {
        _elements.Clear();
        _elements.AddRange(elements);
        if (_selected != null && !_elements.Contains(_selected)) _selected = null;
    }

    private abstract record HistoryOp;
    private sealed record ListOp(DrawElement[] Before, DrawElement[] After) : HistoryOp;
    private sealed record MoveOp(DrawElement Element, double Dx, double Dy) : HistoryOp;
}
