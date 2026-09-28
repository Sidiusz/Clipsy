using Clipsy.Drawing;
using Windows.Foundation;
using Xunit;

namespace Clipsy.Tests;

public class DrawingHistoryTests
{
    private static StrokeElement Stroke(params (double x, double y)[] pts)
        => new() { Points = pts.Select(p => new Point(p.x, p.y)).ToList(), Thickness = 2, Color = Microsoft.UI.Colors.Red };

    [Fact]
    public void EraseIsUndoableAndRedoable()
    {
        var d = new DrawingController();
        var rect = new RectangleElement { Bounds = new Rect(0, 0, 50, 50), Thickness = 2 };
        d.Add(rect);

        Assert.True(d.WholeStrokeErase(new Point(0, 25), 3));
        Assert.Empty(d.Elements);

        Assert.True(d.Undo());
        Assert.Same(rect, Assert.Single(d.Elements));
        Assert.True(d.Redo());
        Assert.Empty(d.Elements);
        Assert.True(d.Undo());
        Assert.True(d.Undo());
        Assert.Empty(d.Elements);
    }

    [Fact]
    public void EraseGestureIsOneUndoStep()
    {
        var d = new DrawingController();
        d.Add(Stroke((0, 0), (100, 0)));
        d.Add(Stroke((0, 50), (100, 50)));

        d.BeginEdit();
        d.PartialErase(new Point(50, 0), 3);
        d.PartialErase(new Point(50, 50), 3);
        d.EndEdit();
        Assert.Equal(4, d.Elements.Count); // both strokes split in two

        Assert.True(d.Undo());
        Assert.Equal(2, d.Elements.Count);
    }

    [Fact]
    public void PartialEraseCutsLongSparseSegment()
    {
        var d = new DrawingController();
        d.Add(Stroke((0, 0), (200, 0))); // two points, eraser in the middle

        Assert.True(d.PartialErase(new Point(100, 0), 4));
        Assert.Equal(2, d.Elements.Count);
        var left = (StrokeElement)d.Elements.OrderBy(e => e.BoundingBox.X).First();
        Assert.True(left.BoundingBox.Right < 100);
    }

    [Fact]
    public void ClearDrawingsIsUndoable()
    {
        var d = new DrawingController();
        d.Add(Stroke((0, 0), (10, 10)));
        d.Add(Stroke((5, 5), (20, 20)));

        Assert.True(d.ClearDrawings());
        Assert.Empty(d.Elements);
        Assert.True(d.Undo());
        Assert.Equal(2, d.Elements.Count);
    }

    [Fact]
    public void MoveIsUndoable()
    {
        var d = new DrawingController();
        var text = new TextElement { Position = new Point(10, 10), Text = "hi", FontSize = 12 };
        d.Add(text);

        text.Offset(30, 5);
        d.RecordMove(text, 30, 5);
        Assert.True(d.Undo());
        Assert.Equal(new Point(10, 10), text.Position);
        Assert.True(d.Redo());
        Assert.Equal(new Point(40, 15), text.Position);
    }
}
