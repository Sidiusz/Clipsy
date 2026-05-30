using System;
using System.Collections.Generic;
using System.Drawing;

namespace Clipsy.Drawing;

/// <summary>
/// Surface-agnostic pencil + eraser logic. Owns the stroke list, current
/// brush settings, and the cursor position. Both the WinUI capture overlay
/// and the Win32 layered recording overlay subscribe via <see cref="Changed"/>
/// and re-paint from <see cref="Strokes"/> on each tick. The engine itself
/// never touches a rendering API, so it can be reused unchanged on any
/// surface (XAML Canvas, GDI DIB, future Direct2D, etc.).
/// </summary>
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

    private void SaveHistory()
    {
        var snapshot = new List<Stroke>(_strokes.Count);
        foreach (var s in _strokes)
            snapshot.Add(new Stroke { Color = s.Color, Thickness = s.Thickness, Points = new List<System.Drawing.PointF>(s.Points) });
        _history.Push(snapshot);
    }

    public void BeginErase(float x, float y, bool wholeStroke)
    {
        _erasing = true;
        _eraseWholeStroke = wholeStroke;
        if (_strokes.Count > 0) SaveHistory();
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
        // History was saved in EraseAt on first hit; nothing to do here.
        Changed?.Invoke();
    }

    private void EraseAt(float x, float y)
    {
        bool changed = _eraseWholeStroke ? EraseWhole(x, y) : EraseSplit(x, y);
        if (changed) Changed?.Invoke();
    }

    private bool EraseWhole(float x, float y)
    {
        bool removed = false;
        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            var s = _strokes[i];
            float hit = _eraserRadius + s.Thickness / 2f;
            float hit2 = hit * hit;
            foreach (var p in s.Points)
            {
                float dx = p.X - x, dy = p.Y - y;
                if (dx * dx + dy * dy <= hit2)
                {
                    _strokes.RemoveAt(i);
                    removed = true;
                    break;
                }
            }
        }
        return removed;
    }

    private bool EraseSplit(float x, float y)
    {
        bool changed = false;
        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            var s = _strokes[i];
            float hit = _eraserRadius + s.Thickness / 2f;
            float hit2 = hit * hit;
            var runs = new List<List<System.Drawing.PointF>>();
            List<System.Drawing.PointF>? run = null;
            bool anyHit = false;
            foreach (var p in s.Points)
            {
                float dx = p.X - x, dy = p.Y - y;
                bool inside = dx * dx + dy * dy <= hit2;
                if (inside)
                {
                    anyHit = true;
                    if (run != null) { runs.Add(run); run = null; }
                }
                else
                {
                    run ??= new List<System.Drawing.PointF>();
                    run.Add(p);
                }
            }
            if (run != null) runs.Add(run);
            if (!anyHit) continue;
            changed = true;
            _strokes.RemoveAt(i);
            foreach (var r in runs)
            {
                if (r.Count < 2) continue;
                _strokes.Insert(i, new Stroke { Color = s.Color, Thickness = s.Thickness, Points = r });
            }
        }
        return changed;
    }
}
