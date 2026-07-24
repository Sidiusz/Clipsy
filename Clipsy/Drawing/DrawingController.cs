using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.UI;

namespace Clipsy.Drawing;

/// <summary>Vector elements drawn over the selected region plus undo/redo.
/// Committed elements render on a GPU Win2D canvas (redrawn only on change),
/// so the XAML tree stays flat regardless of how much is drawn.</summary>
public sealed class DrawingController
{
    private readonly CanvasControl _canvas;
    private readonly List<DrawElement> _elements = new();
    private readonly Stack<HistoryOp> _undo = new();
    private readonly Stack<HistoryOp> _redo = new();

    private static readonly CanvasStrokeStyle RoundStroke = new()
    {
        StartCap = CanvasCapStyle.Round,
        EndCap = CanvasCapStyle.Round,
        LineJoin = CanvasLineJoin.Round,
    };

    // Committed content is baked into an offscreen target and rebuilt only on
    // change, so an active-draw frame is one blit — flat FPS no matter how much
    // is already drawn.
    private CanvasRenderTarget? _cache;
    private bool _cacheDirty = true;

    // In-progress pencil stroke: each new segment is painted straight into the
    // cache (O(1) per move), never a per-frame geometry rebuild from all points.
    private bool _activeOpen;
    private bool _activeMissedPaint;
    private Color _activeColor;
    private double _activeThickness;
    private Point _activeLast;

    public DrawingSettings Settings { get; } = new();
    public IReadOnlyList<DrawElement> Elements => _elements;

    public DrawingController(CanvasControl canvas)
    {
        _canvas = canvas;
        _canvas.Draw += OnDraw;
    }

    // Committed content changed (move/undo/etc.): rebuild the cache next draw.
    public void InvalidateCommitted() { _cacheDirty = true; _canvas.Invalidate(); }

    public void BeginActiveStroke(Color color, double thickness, Point start)
    {
        _activeColor = color;
        _activeThickness = thickness;
        _activeLast = start;
        _activeOpen = true;
        _activeMissedPaint = false;
        PaintActiveSegment(start, start); // start dot (round cap)
    }

    public void SetActiveThickness(double thickness) => _activeThickness = thickness;

    public void AppendActiveStroke(Point pt)
    {
        if (!_activeOpen) return;
        PaintActiveSegment(_activeLast, pt);
        _activeLast = pt;
    }

    // Commit: pixels are already in the cache from the incremental paint, so add
    // the element without a full rebuild. Undo/erase later rebuild from the list.
    public void EndActiveStroke(StrokeElement e)
    {
        _activeOpen = false;
        _elements.Add(e);
        _undo.Push(new HistoryOp(HistoryKind.Add, e));
        _redo.Clear();
        // Cache wasn't ready for some segments — rebuild so the stroke shows.
        if (_activeMissedPaint) InvalidateCommitted();
    }

    // Discard an in-progress stroke: repaint the cache from committed elements.
    public void CancelActiveStroke()
    {
        if (!_activeOpen) return;
        _activeOpen = false;
        InvalidateCommitted();
    }

    private void PaintActiveSegment(Point a, Point b)
    {
        if (_cache == null) { _activeMissedPaint = true; _canvas.Invalidate(); return; }
        using var cds = _cache.CreateDrawingSession();
        cds.DrawLine(V(a), V(b), _activeColor, (float)_activeThickness, RoundStroke);
        _canvas.Invalidate();
    }

    public void DisposeResources()
    {
        _cache?.Dispose();
        _cache = null;
    }

    public void Add(DrawElement e)
    {
        _elements.Add(e);
        _undo.Push(new HistoryOp(HistoryKind.Add, e));
        _redo.Clear();
        InvalidateCommitted();
    }

    public void Remove(DrawElement e)
    {
        if (!_elements.Remove(e)) return;
        _undo.Push(new HistoryOp(HistoryKind.Remove, e));
        _redo.Clear();
        InvalidateCommitted();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var op = _undo.Pop();
        ApplyInverse(op);
        _redo.Push(op);
        InvalidateCommitted();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var op = _redo.Pop();
        Apply(op);
        _undo.Push(op);
        InvalidateCommitted();
        return true;
    }

    public void ClearAll()
    {
        _elements.Clear();
        _undo.Clear();
        _redo.Clear();
        InvalidateCommitted();
    }

    public DrawElement? HitTestTopmost(Point p, double radius)
    {
        for (int i = _elements.Count - 1; i >= 0; i--)
        {
            if (_elements[i].HitTest(p, radius)) return _elements[i];
        }
        return null;
    }

    /// <summary>Removes any element the cursor touches, whole (strokes, shapes,
    /// lines, text).</summary>
    public bool WholeStrokeErase(Point cursor, double radius)
    {
        bool changed = false;
        for (int i = _elements.Count - 1; i >= 0; i--)
        {
            var el = _elements[i];
            if (!el.HitTest(cursor, radius)) continue;
            _elements.RemoveAt(i);
            changed = true;
        }
        if (changed) { _redo.Clear(); InvalidateCommitted(); }
        return changed;
    }

    /// <summary>Eraser pass at the cursor: pencil strokes split (points within
    /// radius dropped, survivors re-polylined); shapes/text removed whole on touch.</summary>
    public bool PartialErase(Point cursor, double radius)
    {
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
        if (changed)
        {
            // Eraser commits make the existing history meaningless for the
            // affected stroke; clear redo and skip undo bookkeeping.
            _redo.Clear();
            InvalidateCommitted();
        }
        return changed;
    }

    private static List<StrokeElement>? SplitStrokeAroundEraser(StrokeElement stroke, Point cursor, double radius)
    {
        double reach = radius + stroke.Thickness * 0.5;
        double r2 = reach * reach;
        var keep = new bool[stroke.Points.Count];
        bool anyHit = false;
        for (int i = 0; i < stroke.Points.Count; i++)
        {
            var p = stroke.Points[i];
            var dx = p.X - cursor.X;
            var dy = p.Y - cursor.Y;
            bool hit = dx * dx + dy * dy <= r2;
            keep[i] = !hit;
            if (hit) anyHit = true;
        }
        if (!anyHit) return null;

        var results = new List<StrokeElement>();
        var run = new List<Point>();
        for (int i = 0; i < stroke.Points.Count; i++)
        {
            if (keep[i])
            {
                run.Add(stroke.Points[i]);
            }
            else
            {
                if (run.Count >= 2) results.Add(CloneStrokeWithPoints(stroke, run));
                run = new List<Point>();
            }
        }
        if (run.Count >= 2) results.Add(CloneStrokeWithPoints(stroke, run));
        return results;
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
        // One bad element (e.g. an unresolvable font) must not blank the cache.
        foreach (var el in _elements)
        {
            try { DrawOne(cds, el); } catch { }
        }
    }

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
        using var fmt = new CanvasTextFormat
        {
            FontFamily = Win2DFontFamily(t.FontFamily),
            FontSize = (float)t.FontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        ds.DrawText(t.Text, V(t.Position), t.Color, fmt);
    }

    // Win2D wants a single family or "uri#family" — not a CSS-style comma list.
    private static string Win2DFontFamily(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "Segoe UI";
        var first = source.Split(',')[0].Trim();
        if (string.IsNullOrEmpty(first) ||
            first.Equals("sans-serif", StringComparison.OrdinalIgnoreCase))
            return "Segoe UI";
        return first;
    }

    // Open arrowhead matching the live preview geometry.
    private static (Point, Point) ArrowHead(Point a, Point b, double thickness)
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

    private void Apply(HistoryOp op)
    {
        if (op.Kind == HistoryKind.Add) _elements.Add(op.Element);
        else _elements.Remove(op.Element);
    }

    private void ApplyInverse(HistoryOp op)
    {
        if (op.Kind == HistoryKind.Add) _elements.Remove(op.Element);
        else _elements.Add(op.Element);
    }

    private enum HistoryKind { Add, Remove }
    private readonly record struct HistoryOp(HistoryKind Kind, DrawElement Element);
}
