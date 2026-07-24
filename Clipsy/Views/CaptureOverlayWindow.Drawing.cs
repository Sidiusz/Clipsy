using System;
using System.Collections.Generic;
using Clipsy.Drawing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;

namespace Clipsy.Views;

public sealed partial class CaptureOverlayWindow
{
    // ---------- Drawing tools ----------

    private void StartToolPress(Point pos, Pointer pointer)
    {
        // Drawings live in root DIPs so they stay fixed on screen when the
        // selection rectangle moves or resizes.
        switch (_drawing.Settings.Tool)
        {
            case ToolKind.Pencil:
                _mode = InteractionMode.DrawingStroke;
                _activeStrokeVisual = new Polyline
                {
                    Stroke = new SolidColorBrush(_drawing.Settings.Color),
                    StrokeThickness = _drawing.Settings.PencilThickness,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                };
                _activeStrokeVisual.Points.Add(pos);
                _activeStroke = new StrokeElement
                {
                    Visual = _activeStrokeVisual,
                    Points = new List<Point> { pos },
                    Thickness = _drawing.Settings.PencilThickness,
                };
                DrawingCanvas.Children.Add(_activeStrokeVisual);
                RootGrid.CapturePointer(pointer);
                break;
            case ToolKind.Rectangle:
                _mode = InteractionMode.DrawingRect;
                _activeRectAnchor = pos;
                _activeRectVisual = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Stroke = new SolidColorBrush(_drawing.Settings.Color),
                    StrokeThickness = _drawing.Settings.RectangleThickness,
                    Width = 0,
                    Height = 0,
                };
                Canvas.SetLeft(_activeRectVisual, pos.X);
                Canvas.SetTop(_activeRectVisual, pos.Y);
                DrawingCanvas.Children.Add(_activeRectVisual);
                RootGrid.CapturePointer(pointer);
                break;
            case ToolKind.Ellipse:
                _mode = InteractionMode.DrawingRect;
                _activeRectAnchor = pos;
                _activeRectVisual = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Stroke = new SolidColorBrush(_drawing.Settings.Color),
                    StrokeThickness = _drawing.Settings.EllipseThickness,
                    Width = 0,
                    Height = 0,
                };
                Canvas.SetLeft(_activeRectVisual, pos.X);
                Canvas.SetTop(_activeRectVisual, pos.Y);
                DrawingCanvas.Children.Add(_activeRectVisual);
                RootGrid.CapturePointer(pointer);
                break;
            case ToolKind.Line:
                _mode = InteractionMode.DrawingRect;
                _activeLineVisual = new Line
                {
                    Stroke = new SolidColorBrush(_drawing.Settings.Color),
                    StrokeThickness = _drawing.Settings.LineThickness,
                    X1 = pos.X,
                    Y1 = pos.Y,
                    X2 = pos.X,
                    Y2 = pos.Y,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                };
                DrawingCanvas.Children.Add(_activeLineVisual);
                RootGrid.CapturePointer(pointer);
                break;
            case ToolKind.Arrow:
                _mode = InteractionMode.DrawingRect;
                _activeRectAnchor = pos;
                _activeArrowEnd = pos;
                _activeArrowVisual = BuildArrowVisual(pos, pos,
                    new SolidColorBrush(_drawing.Settings.Color),
                    _drawing.Settings.LineThickness);
                DrawingCanvas.Children.Add(_activeArrowVisual);
                RootGrid.CapturePointer(pointer);
                break;
            case ToolKind.Text:
                // Click-to-place: no drag mode, no pointer capture. The TextBox
                // keeps focus until LostFocus / Enter / Esc.
                StartTextEntry(pos);
                break;
        }
    }

    // ---------- Arrow geometry ----------

    private Microsoft.UI.Xaml.Shapes.Path? _activeArrowVisual;
    private Point _activeArrowEnd;

    private static Microsoft.UI.Xaml.Shapes.Path BuildArrowVisual(Point start, Point end, Brush stroke, double thickness)
    {
        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = BuildArrowGeometry(start, end, thickness),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
    }

    private static Geometry BuildArrowGeometry(Point start, Point end, double thickness)
    {
        var geo = new PathGeometry();
        var shaft = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        shaft.Segments.Add(new LineSegment { Point = end });
        geo.Figures.Add(shaft);

        var (h1, h2) = ArrowHeadPoints(start, end, thickness);
        var head = new PathFigure { StartPoint = h1, IsClosed = false, IsFilled = false };
        head.Segments.Add(new LineSegment { Point = end });
        head.Segments.Add(new LineSegment { Point = h2 });
        geo.Figures.Add(head);
        return geo;
    }

    private static (Point, Point) ArrowHeadPoints(Point a, Point b, double thickness)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = System.Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-3) return (b, b);
        double ux = dx / len, uy = dy / len;
        // Head scales with the brush but never longer than the shaft itself.
        double headLen = System.Math.Min(System.Math.Max(9.0, thickness * 3.0), len);
        const double spread = 0.46; // ~26° per side
        double cos = System.Math.Cos(spread), sin = System.Math.Sin(spread);
        double bx = -ux, by = -uy;
        var p1 = new Point(b.X + headLen * (bx * cos - by * sin), b.Y + headLen * (bx * sin + by * cos));
        var p2 = new Point(b.X + headLen * (bx * cos + by * sin), b.Y + headLen * (-bx * sin + by * cos));
        return (p1, p2);
    }

    // Drop points closer than ~1.4 px to the last one. High-poll mice + intermediate
    // points otherwise pile thousands of sub-pixel points into one Polyline, and
    // WinUI re-tessellates the whole thing every frame — the continuous-draw lag.
    private const double MinStrokePointDistSq = 2.0;

    private void ExtendStroke(Point pos)
    {
        if (_activeStroke == null || _activeStrokeVisual == null) return;
        var pts = _activeStroke.Points;
        if (pts.Count > 0)
        {
            var last = pts[pts.Count - 1];
            double dx = pos.X - last.X, dy = pos.Y - last.Y;
            if (dx * dx + dy * dy < MinStrokePointDistSq) return;
        }
        _activeStroke.Points.Add(pos);
        _activeStrokeVisual.Points.Add(pos);
    }

    private void FinishStroke()
    {
        if (_activeStroke == null || _activeStrokeVisual == null) return;
        // Single click → zero-length stroke renders nothing; add a 0.01-px
        // sibling point so the round cap paints a visible dot.
        if (_activeStroke.Points.Count == 1)
        {
            var only = _activeStroke.Points[0];
            var twin = new Point(only.X + 0.01, only.Y + 0.01);
            _activeStroke.Points.Add(twin);
            _activeStrokeVisual.Points.Add(twin);
        }
        DrawingCanvas.Children.Remove(_activeStrokeVisual);
        _drawing.Add(_activeStroke);
        _activeStroke = null;
        _activeStrokeVisual = null;
    }

    private void UpdateActiveShape(Point pos)
    {
        if (_activeArrowVisual != null)
        {
            _activeArrowEnd = pos;
            _activeArrowVisual.Data = BuildArrowGeometry(_activeRectAnchor, pos, _activeArrowVisual.StrokeThickness);
            return;
        }

        if (_activeLineVisual != null)
        {
            _activeLineVisual.X2 = pos.X;
            _activeLineVisual.Y2 = pos.Y;
            return;
        }

        if (_activeRectVisual == null) return;
        double x = System.Math.Min(_activeRectAnchor.X, pos.X);
        double y = System.Math.Min(_activeRectAnchor.Y, pos.Y);
        double w = System.Math.Abs(pos.X - _activeRectAnchor.X);
        double h = System.Math.Abs(pos.Y - _activeRectAnchor.Y);
        Canvas.SetLeft(_activeRectVisual, x);
        Canvas.SetTop(_activeRectVisual, y);
        // Below the stroke thickness an ellipse degenerates into a strip; hide
        // it until dragged to a usable size to avoid the "circle = line" artifact.
        double minSide = System.Math.Max(2.0, _activeRectVisual.StrokeThickness);
        _activeRectVisual.Visibility = (w < minSide || h < minSide)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _activeRectVisual.Width = w;
        _activeRectVisual.Height = h;
    }

    private void FinishActiveShape()
    {
        if (_activeArrowVisual != null)
        {
            var a = _activeRectAnchor;
            var b = _activeArrowEnd;
            double thickness = _activeArrowVisual.StrokeThickness;
            var stroke = _activeArrowVisual.Stroke;
            DrawingCanvas.Children.Remove(_activeArrowVisual);
            _activeArrowVisual = null;
            if (System.Math.Abs(b.X - a.X) < 1 && System.Math.Abs(b.Y - a.Y) < 1) return;
            var visual = BuildArrowVisual(a, b, stroke, thickness);
            _drawing.Add(new LineElement
            {
                Visual = visual,
                Start = a,
                End = b,
                Thickness = thickness,
                EndArrow = true,
            });
            return;
        }

        if (_activeLineVisual != null)
        {
            double x1 = _activeLineVisual.X1;
            double y1 = _activeLineVisual.Y1;
            double x2 = _activeLineVisual.X2;
            double y2 = _activeLineVisual.Y2;
            DrawingCanvas.Children.Remove(_activeLineVisual);
            if (System.Math.Abs(x2 - x1) < 1 && System.Math.Abs(y2 - y1) < 1)
            {
                _activeLineVisual = null;
                return;
            }
            var visual = new Line
            {
                Stroke = _activeLineVisual.Stroke,
                StrokeThickness = _activeLineVisual.StrokeThickness,
                X1 = x1,
                Y1 = y1,
                X2 = x2,
                Y2 = y2,
                StrokeStartLineCap = _activeLineVisual.StrokeStartLineCap,
                StrokeEndLineCap = _activeLineVisual.StrokeEndLineCap,
                StrokeLineJoin = _activeLineVisual.StrokeLineJoin,
            };
            var element = new LineElement
            {
                Visual = visual,
                Start = new Point(x1, y1),
                End = new Point(x2, y2),
                Thickness = _activeLineVisual.StrokeThickness,
            };
            _drawing.Add(element);
            _activeLineVisual = null;
            return;
        }

        if (_activeRectVisual == null) return;
        double x = Canvas.GetLeft(_activeRectVisual);
        double y = Canvas.GetTop(_activeRectVisual);
        double w = _activeRectVisual.Width;
        double h = _activeRectVisual.Height;
        DrawingCanvas.Children.Remove(_activeRectVisual);
        if (w < 2 || h < 2) { _activeRectVisual = null; return; }

        if (_activeRectVisual is Ellipse)
        {
            var visual = new Ellipse
            {
                Stroke = _activeRectVisual.Stroke,
                StrokeThickness = _activeRectVisual.StrokeThickness,
                Width = w,
                Height = h,
            };
            Canvas.SetLeft(visual, x);
            Canvas.SetTop(visual, y);
            var element = new EllipseElement
            {
                Visual = visual,
                Bounds = new Rect(x, y, w, h),
                Thickness = _activeRectVisual.StrokeThickness,
            };
            _drawing.Add(element);
        }
        else
        {
            var visual = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Stroke = _activeRectVisual.Stroke,
                StrokeThickness = _activeRectVisual.StrokeThickness,
                Width = w,
                Height = h,
            };
            Canvas.SetLeft(visual, x);
            Canvas.SetTop(visual, y);
            var element = new RectangleElement
            {
                Visual = visual,
                Bounds = new Rect(x, y, w, h),
                Thickness = _activeRectVisual.StrokeThickness,
            };
            _drawing.Add(element);
        }
        _activeRectVisual = null;
    }

    private void TryEraseAt(Point rootPos)
    {
        // Partial-erase pencil strokes (drop points inside the disc); shapes/text
        // removed whole on touch. Shift+RMB removes whole strokes too.
        double r = System.Math.Max(2.0, _drawing.Settings.PencilThickness * 0.5);
        bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down)
            == Windows.UI.Core.CoreVirtualKeyStates.Down;
        if (shift) _drawing.WholeStrokeErase(rootPos, r);
        else _drawing.PartialErase(rootPos, r);
    }

    // ---------- Toolbar / tool selection ----------

    private void OnToolToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        ToolKind tool = tb.Name switch
        {
            "PencilBtn" => ToolKind.Pencil,
            "Rectangle" => ToolKind.Rectangle,
            "EllipseBtn" => ToolKind.Ellipse,
            "LineBtn" => ToolKind.Line,
            "TextBtn" => ToolKind.Text,
            _ => ToolKind.None,
        };
        SetTool(tb.IsChecked == true ? tool : ToolKind.None);
        if (tb.IsChecked == true) PopButton(tb);
    }

    // Quick scale-pop on tool select — confirms the click without delaying it.
    // ScaleX/ScaleY on a transform are dependent animations, hence the flag.
    private static void PopButton(FrameworkElement el)
    {
        if (el == null) return;
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        if (el.RenderTransform is not ScaleTransform st)
        {
            st = new ScaleTransform();
            el.RenderTransform = st;
        }

        var sb = new Storyboard();
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var a = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
            a.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 1.0 });
            a.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(55)),
                Value = 1.12,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
            a.KeyFrames.Add(new EasingDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)),
                Value = 1.0,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            });
            Storyboard.SetTarget(a, st);
            Storyboard.SetTargetProperty(a, prop);
            sb.Children.Add(a);
        }
        sb.Begin();
    }

    private void SetTool(ToolKind tool)
    {
        _drawing.Settings.Tool = tool;

        // Swap the Style (not inline brushes): the Selected style's own visual
        // states keep amber on PointerOver instead of reverting to grey.
        var selectedStyle = (Microsoft.UI.Xaml.Style)Application.Current.Resources["ClipsyIconButtonSelected"];
        var normalStyle   = (Microsoft.UI.Xaml.Style)Application.Current.Resources["ClipsyIconButton"];

        PencilBtn.Style = tool == ToolKind.Pencil ? selectedStyle : normalStyle;
        TextBtn.Style   = tool == ToolKind.Text   ? selectedStyle : normalStyle;
        ShapesBtn.Style = tool is ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Line or ToolKind.Arrow
            ? selectedStyle : normalStyle;

        // The Shapes icon glyphs are Stroke-based shapes (not FontIcon
        // glyphs that inherit Foreground), so swap their stroke explicitly.
        var shapesActive = tool is ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Line or ToolKind.Arrow;
        var shapesStroke = Clipsy.Services.ThemeService.GetBrush(
            shapesActive ? "ClipsyAccentBrush" : "ClipsyText2Brush", RootGrid);
        if (ShapeIconRect      != null) ShapeIconRect.Stroke      = shapesStroke;
        if (ShapeIconEllipse   != null) ShapeIconEllipse.Stroke   = shapesStroke;
        if (ShapeIconLine      != null) ShapeIconLine.Stroke      = shapesStroke;
        if (ShapeIconArrowLine != null) ShapeIconArrowLine.Stroke = shapesStroke;
        if (ShapeIconArrowHead != null) ShapeIconArrowHead.Stroke = shapesStroke;

        RectBtn.Visibility = _currentShapeTool == ToolKind.Rectangle ? Visibility.Collapsed : Visibility.Visible;
        EllipseBtn.Visibility = _currentShapeTool == ToolKind.Ellipse ? Visibility.Collapsed : Visibility.Visible;
        LineBtn.Visibility = _currentShapeTool == ToolKind.Line ? Visibility.Collapsed : Visibility.Visible;
        ArrowBtn.Visibility = _currentShapeTool == ToolKind.Arrow ? Visibility.Collapsed : Visibility.Visible;

        ShapeIconRect?.SetValue(UIElement.VisibilityProperty,
            _currentShapeTool is ToolKind.Ellipse or ToolKind.Line or ToolKind.Arrow ? Visibility.Collapsed : Visibility.Visible);
        ShapeIconEllipse?.SetValue(UIElement.VisibilityProperty,
            _currentShapeTool == ToolKind.Ellipse ? Visibility.Visible : Visibility.Collapsed);
        ShapeIconLine?.SetValue(UIElement.VisibilityProperty,
            _currentShapeTool == ToolKind.Line ? Visibility.Visible : Visibility.Collapsed);
        ShapeIconArrow?.SetValue(UIElement.VisibilityProperty,
            _currentShapeTool == ToolKind.Arrow ? Visibility.Visible : Visibility.Collapsed);

        if (tool is ToolKind.Pencil or ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Line or ToolKind.Arrow)
        {
            UpdatePreviewForThickness(_drawing.Settings.BrushSize);
            if (_textPreview != null) _textPreview.Visibility = Visibility.Collapsed;
        }
        else
        {
            _pencilPreview.Visibility = Visibility.Collapsed;
            // Text preview's per-frame visibility is set in PointerMoved; collapse
            // it explicitly when switching to a non-text tool so it doesn't linger.
            if (_textPreview != null && tool != ToolKind.Text)
                _textPreview.Visibility = Visibility.Collapsed;
        }
    }
}
