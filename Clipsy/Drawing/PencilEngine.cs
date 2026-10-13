using System;
using System.Collections.Generic;
using System.Drawing;

namespace Clipsy.Drawing;

/// <summary>Surface-agnostic pencil + eraser logic (stroke list, brush, cursor).
/// Subscribers repaint from <see cref="Strokes"/>; never touches a rendering API.</summary>
public sealed class PencilEngine
{
    private const int MinThickness = 1;
    private const int MaxThickness = 64;

    private readonly List<Stroke> _strokes = new();
    private readonly Stack<List<Stroke>> _history = new();
    private Stroke? _current;
    private bool _drawing;
    private bool _erasing;
    private bool _eraseWholeStroke;
    private System.Drawing.PointF _cursor;
    private bool _cursorVisible;

    private Color _color = Color.Red;
    private float _thickness = 3f;
    private int _eraserRadius = 5;

    public event Action? Changed;

    public IReadOnlyList<Stroke> Strokes => _strokes;
    public Stroke? Current => _current;
    public System.Drawing.PointF Cursor => _cursor;
    public bool CursorVisible => _cursorVisible;
    public Color Color => _color;
    public float Thickness => _thickness;
    public int EraserRadius => _eraserRadius;
    public bool IsDrawing => _drawing;
    public bool IsErasing => _erasing;

    public sealed class Stroke
    {
        public Color Color;
        public float Thickness;
        public List<System.Drawing.PointF> Points = new();
    }

    public void SetColor(byte r, byte g, byte b)
    {
        _color = Color.FromArgb(255, r, g, b);
        Changed?.Invoke();
    }

    public void SetThickness(float t)
    {
        _thickness = Math.Clamp(t, MinThickness, MaxThickness);
        // Eraser radius = half the stroke width so the erase footprint matches
        // the visible ring (diameter == thickness).
        _eraserRadius = Math.Max(2, (int)Math.Round(_thickness * 0.5f));
        Changed?.Invoke();
    }

    public void NudgeThickness(int steps)
    {
        SetThickness(_thickness + steps);
    }

    public void SetCursor(float x, float y, bool visible = true)
    {
        // Every mouse move lands here; an unchanged cursor needs no repaint.
        if (_cursor.X == x && _cursor.Y == y && _cursorVisible == visible) return;
        _cursor = new System.Drawing.PointF(x, y);
        _cursorVisible = visible;
        Changed?.Invoke();
    }

    public void HideCursor()
    {
        if (!_cursorVisible) return;
        _cursorVisible = false;
        Changed?.Invoke();
    }

    public void BeginStroke(float x, float y)
    {
        _drawing = true;
        _current = new Stroke { Color = _color, Thickness = _thickness };
        _current.Points.Add(new System.Drawing.PointF(x, y));
        Changed?.Invoke();
    }

    public void ExtendStroke(float x, float y)
    {
        if (!_drawing || _current == null) return;
        var last = _current.Points[_current.Points.Count - 1];
        if (last.X == x && last.Y == y) return;
        _current.Points.Add(new System.Drawing.PointF(x, y));
        Changed?.Invoke();
    }

    public void EndStroke()
    {
        if (!_drawing) return;
        _drawing = false;
        if (_current != null && _current.Points.Count > 0)
        {
            SaveHistory();
            _strokes.Add(_current);
        }
        _current = null;
        Changed?.Invoke();
    }

    public void ClearAll()
    {
        if (_strokes.Count > 0) SaveHistory();
        _strokes.Clear();
        _current = null;
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (_history.Count == 0) return;
        _strokes.Clear();
        _strokes.AddRange(_history.Pop());
        Changed?.Invoke();
    }

    // Committed strokes are never mutated (erasing replaces them), so a shallow copy is a full snapshot.
    private void SaveHistory() => _history.Push(new List<Stroke>(_strokes));

    private List<Stroke>? _eraseSnapshot;
    private bool _eraseChanged;

    public void BeginErase(float x, float y, bool wholeStroke)
    {
        _erasing = true;
        _eraseWholeStroke = wholeStroke;
        _eraseSnapshot = new List<Stroke>(_strokes);
        _eraseChanged = false;
        EraseAt(x, y);
    }

    public void ExtendErase(float x, float y)
    {
        if (!_erasing) return;
        EraseAt(x, y);
    }

    public void EndErase()
    {
        if (!_erasing) return;
        _erasing = false;
        // One undo step per erase gesture, and none if it touched nothing.
        if (_eraseChanged && _eraseSnapshot != null) _history.Push(_eraseSnapshot);
        _eraseSnapshot = null;
        Changed?.Invoke();
    }

    private void EraseAt(float x, float y)
    {
        bool changed = _eraseWholeStroke ? EraseWhole(x, y) : EraseSplit(x, y);
        if (!changed) return;
        _eraseChanged = true;
        Changed?.Invoke();
    }

    private bool EraseWhole(float x, float y)
    {
        bool removed = false;
        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            if (!HitsStroke(_strokes[i], x, y)) continue;
            _strokes.RemoveAt(i);
            removed = true;
        }
        return removed;
    }

    private bool EraseSplit(float x, float y)
    {
        bool changed = false;
        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            var s = _strokes[i];
            if (!HitsStroke(s, x, y)) continue;
            // A graze between two samples cuts nothing; leave the stroke (and the undo history) alone.
            if (SplitAround(s, x, y) is not { } runs) continue;
            changed = true;
            _strokes.RemoveAt(i);
            foreach (var r in runs)
                _strokes.Insert(i, new Stroke { Color = s.Color, Thickness = s.Thickness, Points = r });
        }
        return changed;
    }

    // Segments, not just points: fast strokes have long gaps between samples.
    private bool HitsStroke(Stroke s, float x, float y)
    {
        float reach = _eraserRadius + s.Thickness / 2f;
        float reach2 = reach * reach;
        var pts = s.Points;
        if (pts.Count == 0) return false;
        if (DistanceSq(pts[0].X, pts[0].Y, x, y) <= reach2) return true;
        for (int k = 1; k < pts.Count; k++)
            if (SegmentDistanceSq(pts[k - 1], pts[k], x, y) <= reach2) return true;
        return false;
    }

    // Densify long segments so the eraser can cut through their middle, then keep the runs outside it.
    // Null when no sample falls inside the eraser.
    private List<List<System.Drawing.PointF>>? SplitAround(Stroke s, float x, float y)
    {
        float reach = _eraserRadius + s.Thickness / 2f;
        float reach2 = reach * reach;
        float step = Math.Max(1f, reach * 0.5f);
        var src = s.Points;
        var pts = new List<System.Drawing.PointF>(src.Count * 2) { src[0] };
        for (int k = 1; k < src.Count; k++)
        {
            var a = src[k - 1];
            var b = src[k];
            float len = MathF.Sqrt(DistanceSq(a.X, a.Y, b.X, b.Y));
            int n = (int)(len / step);
            for (int j = 1; j < n; j++)
            {
                float t = j / (float)n;
                pts.Add(new System.Drawing.PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
            }
            pts.Add(b);
        }

        var runs = new List<List<System.Drawing.PointF>>();
        var run = new List<System.Drawing.PointF>();
        bool cut = false;
        foreach (var p in pts)
        {
            if (DistanceSq(p.X, p.Y, x, y) > reach2)
            {
                run.Add(p);
                continue;
            }
            cut = true;
            if (run.Count >= 2) runs.Add(run);
            run = new List<System.Drawing.PointF>();
        }
        if (!cut) return null;
        if (run.Count >= 2) runs.Add(run);
        return runs;
    }

    private static float DistanceSq(float ax, float ay, float bx, float by)
    {
        float dx = ax - bx, dy = ay - by;
        return dx * dx + dy * dy;
    }

    private static float SegmentDistanceSq(System.Drawing.PointF a, System.Drawing.PointF b, float px, float py)
    {
        float vx = b.X - a.X, vy = b.Y - a.Y;
        float len2 = vx * vx + vy * vy;
        float t = len2 < 1e-6f ? 0 : Math.Clamp(((px - a.X) * vx + (py - a.Y) * vy) / len2, 0f, 1f);
        return DistanceSq(a.X + vx * t, a.Y + vy * t, px, py);
    }
}
