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
