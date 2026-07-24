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

    // In-progress pencil stroke, drawn on the GPU on top of committed content so
    // a single long stroke never grows a XAML Polyline (the continuous-draw lag).
    private StrokeElement? _activePreview;

    public DrawingSettings Settings { get; } = new();
    public IReadOnlyList<DrawElement> Elements => _elements;

    public DrawingController(CanvasControl canvas)
    {
        _canvas = canvas;
        _canvas.Draw += OnDraw;
    }

    public void Invalidate() => _canvas.Invalidate();

    public void SetActivePreview(StrokeElement? s) { _activePreview = s; _canvas.Invalidate(); }

    public void Add(DrawElement e)
    {
        _elements.Add(e);
        _undo.Push(new HistoryOp(HistoryKind.Add, e));
        _redo.Clear();
        _canvas.Invalidate();
    }

    public void Remove(DrawElement e)
    {
        if (!_elements.Remove(e)) return;
        _undo.Push(new HistoryOp(HistoryKind.Remove, e));
        _redo.Clear();
        _canvas.Invalidate();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var op = _undo.Pop();
        ApplyInverse(op);
        _redo.Push(op);
        _canvas.Invalidate();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var op = _redo.Pop();
        Apply(op);
        _undo.Push(op);
        _canvas.Invalidate();
        return true;
    }

    public void ClearAll()
    {
        _elements.Clear();
        _undo.Clear();
        _redo.Clear();
        _canvas.Invalidate();
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
        if (changed) { _redo.Clear(); _canvas.Invalidate(); }
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
            _canvas.Invalidate();
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
        // One bad element (e.g. an unresolvable font) must not blank the canvas.
        foreach (var el in _elements)
        {
            try { DrawOne(ds, el); } catch { }
        }
        if (_activePreview != null)
        {
            try { DrawStroke(ds, _activePreview); } catch { }
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
