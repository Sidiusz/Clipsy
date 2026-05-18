using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Clipsy.Drawing;

/// <summary>
/// Maintains the list of vector elements drawn on top of the selected region
/// plus an undo/redo history. Visuals are added/removed from the supplied host
/// canvas; geometry is stored in canvas-local coordinates.
/// </summary>
public sealed class DrawingController
{
    private readonly Canvas _host;
    private readonly List<DrawElement> _elements = new();
    private readonly Stack<HistoryOp> _undo = new();
    private readonly Stack<HistoryOp> _redo = new();

    public DrawingSettings Settings { get; } = new();
    public IReadOnlyList<DrawElement> Elements => _elements;

    public DrawingController(Canvas host) { _host = host; }

    public void Add(DrawElement e)
    {
        _elements.Add(e);
        _host.Children.Add(e.Visual);
        _undo.Push(new HistoryOp(HistoryKind.Add, e));
        _redo.Clear();
    }

    public void Remove(DrawElement e)
    {
        if (!_elements.Remove(e)) return;
        _host.Children.Remove(e.Visual);
        _undo.Push(new HistoryOp(HistoryKind.Remove, e));
        _redo.Clear();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var op = _undo.Pop();
        ApplyInverse(op);
        _redo.Push(op);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        var op = _redo.Pop();
        Apply(op);
        _undo.Push(op);
        return true;
    }

    public void ClearAll()
    {
        foreach (var e in _elements)
        {
            _host.Children.Remove(e.Visual);
        }
        _elements.Clear();
        _undo.Clear();
        _redo.Clear();
    }

    public DrawElement? HitTestTopmost(Point p, double radius)
    {
        for (int i = _elements.Count - 1; i >= 0; i--)
        {
            if (_elements[i].HitTest(p, radius)) return _elements[i];
        }
        return null;
    }

    /// <summary>
    /// Eraser pass at the given root-space cursor. Pencil strokes are split:
    /// every stroke point within <paramref name="radius"/> of the cursor is
    /// dropped and the surviving runs become their own polylines. Rectangles
    /// and text fall back to whole-element removal on touch since they are
    /// not point-sampled.
    /// </summary>
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
                    _host.Children.Remove(s.Visual);
                    _elements.RemoveAt(i);
                    foreach (var sub in splits)
                    {
                        _elements.Insert(i, sub);
                        _host.Children.Add(sub.Visual);
                    }
                    break;
                default:
                    if (el.HitTest(cursor, radius))
                    {
                        _host.Children.Remove(el.Visual);
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
    {
        var poly = new Polyline();
        if (orig.Visual is Polyline op)
        {
            poly.Stroke = op.Stroke;
            poly.StrokeThickness = op.StrokeThickness;
            poly.StrokeStartLineCap = op.StrokeStartLineCap;
            poly.StrokeEndLineCap = op.StrokeEndLineCap;
            poly.StrokeLineJoin = op.StrokeLineJoin;
        }
        foreach (var p in pts) poly.Points.Add(p);
        return new StrokeElement
        {
            Visual = poly,
            Points = pts,
            Thickness = orig.Thickness,
        };
    }

    private void Apply(HistoryOp op)
    {
        if (op.Kind == HistoryKind.Add)
        {
            _elements.Add(op.Element);
            _host.Children.Add(op.Element.Visual);
        }
        else
        {
            _elements.Remove(op.Element);
            _host.Children.Remove(op.Element.Visual);
        }
    }

    private void ApplyInverse(HistoryOp op)
    {
        if (op.Kind == HistoryKind.Add)
        {
            _elements.Remove(op.Element);
            _host.Children.Remove(op.Element.Visual);
        }
        else
        {
            _elements.Add(op.Element);
            _host.Children.Add(op.Element.Visual);
        }
    }

    private enum HistoryKind { Add, Remove }
    private readonly record struct HistoryOp(HistoryKind Kind, DrawElement Element);
}
