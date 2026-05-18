using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IOPath = System.IO.Path;
using Clipsy.Drawing;
using Clipsy.Localization;
using Clipsy.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;

namespace Clipsy.Views;

public sealed partial class CaptureOverlayWindow : Window
{
    private enum InteractionMode { Idle, SelectingNew, MovingSelection, ResizingSelection, DrawingStroke, DrawingRect, Erasing, PlacingText, SelectingOcrText }
    private enum HandlePos { TL, T, TR, R, BR, B, BL, L }

    private const double MinSelectionSize = 4.0;
    private const double SingleClickFallbackSize = 100.0;
    private const double HandleSize = 10.0;
    private const double HandleHitInflate = 6.0;
    private const double EraserRadius = 4.0;

    private readonly ScreenFreezeService.FrozenFrame _frame;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly DrawingController _drawing;
    private readonly List<Microsoft.UI.Xaml.Shapes.Rectangle> _handleVisuals = new();
    private readonly Ellipse _pencilPreview;

    private InteractionMode _mode = InteractionMode.Idle;
    private bool _hasSelection;
    private Rect _selectionRect;
    private Rect _selectionAtDragStart;
    private Point _dragStart;
    private HandlePos _activeHandle;

    private Polyline? _activeStrokeVisual;
    private StrokeElement? _activeStroke;
    private Microsoft.UI.Xaml.Shapes.Rectangle? _activeRectVisual;
    private Point _activeRectAnchor;
    private TextBox? _activeTextBox;

    // OCR state
    private bool _inOcrMode;
    private readonly List<(Rect bounds, Microsoft.UI.Xaml.Shapes.Rectangle box, TextBlock glyph)> _ocrVisuals = new();
    private readonly List<OcrWord> _ocrWordsRaw = new();
    private readonly List<Rect> _ocrWordsDip = new();
    private readonly HashSet<int> _ocrSelected = new();
    private DispatcherTimer? _scanTimer;
    private double _scanY;
    private int _scanDir = 1;
    private Point _ocrDragStart;

    public CaptureOverlayWindow(ScreenFreezeService.FrozenFrame frame)
    {
        _frame = frame;
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = GetAppWindowForCurrentWindow();
        ConfigureAsOverlay();

        _drawing = new DrawingController(DrawingCanvas);
        Hint.Text = Strings.Get("HintSelectArea");
        BuildHandles();
        _pencilPreview = new Ellipse
        {
            Width = 12,
            Height = 12,
            Stroke = new SolidColorBrush(Microsoft.UI.Colors.White),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        CursorPreviewLayer.Children.Add(_pencilPreview);

        BuildScreenMenu();
        Activated += OnActivated;
    }

    private AppWindow GetAppWindowForCurrentWindow()
    {
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
        return AppWindow.GetFromWindowId(id);
    }

    private void ConfigureAsOverlay()
    {
        if (_appWindow.Presenter is OverlappedPresenter op)
        {
            op.SetBorderAndTitleBar(false, false);
            op.IsResizable = false;
            op.IsMaximizable = false;
            op.IsMinimizable = false;
            op.IsAlwaysOnTop = true;
        }
        var b = _frame.VirtualBounds;
        _appWindow.MoveAndResize(new RectInt32(b.X, b.Y, b.Width, b.Height));
        UpdateDimGeometry(null);
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (FrozenImage.Source == null)
        {
            FrozenImage.Source = await ScreenFreezeService.ToBitmapImageAsync(_frame.PngBytes);
        }
        RootGrid.Focus(FocusState.Programmatic);
    }

    // ---------- Handles ----------

    private void BuildHandles()
    {
        for (int i = 0; i < 8; i++)
        {
            var r = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
                Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0x1F, 0x6F, 0xEB)),
                StrokeThickness = 1,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            HandlesLayer.Children.Add(r);
            _handleVisuals.Add(r);
        }
    }

    private void PositionHandles()
    {
        if (!_hasSelection)
        {
            foreach (var hv in _handleVisuals) hv.Visibility = Visibility.Collapsed;
            return;
        }
        double w = _selectionRect.Width, h = _selectionRect.Height;
        var anchors = new (double X, double Y)[]
        {
            (0, 0), (w / 2, 0), (w, 0),
            (w, h / 2),
            (w, h), (w / 2, h), (0, h),
            (0, h / 2),
        };
        for (int i = 0; i < 8; i++)
        {
            Canvas.SetLeft(_handleVisuals[i], anchors[i].X - HandleSize / 2);
            Canvas.SetTop(_handleVisuals[i], anchors[i].Y - HandleSize / 2);
            _handleVisuals[i].Visibility = Visibility.Visible;
        }
    }

    private bool TryGetHandle(Point rootPos, out HandlePos handle)
    {
        handle = HandlePos.TL;
        if (!_hasSelection) return false;
        double w = _selectionRect.Width, h = _selectionRect.Height;
        var local = new Point(rootPos.X - _selectionRect.X, rootPos.Y - _selectionRect.Y);
        var anchors = new (double X, double Y, HandlePos H)[]
        {
            (0, 0, HandlePos.TL), (w / 2, 0, HandlePos.T), (w, 0, HandlePos.TR),
            (w, h / 2, HandlePos.R),
            (w, h, HandlePos.BR), (w / 2, h, HandlePos.B), (0, h, HandlePos.BL),
            (0, h / 2, HandlePos.L),
        };
        double half = HandleSize / 2 + HandleHitInflate;
        foreach (var a in anchors)
        {
            if (System.Math.Abs(local.X - a.X) <= half && System.Math.Abs(local.Y - a.Y) <= half)
            {
                handle = a.H;
                return true;
            }
        }
        return false;
    }

    // ---------- Pointer input ----------

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var cp = e.GetCurrentPoint(RootGrid);
        var pos = cp.Position;
        bool rmb = cp.Properties.IsRightButtonPressed;
        bool lmb = cp.Properties.IsLeftButtonPressed;

        if (_inOcrMode)
        {
            // OCR mode owns the overlay. Text selection happens inside the
            // floating OcrTextBox; clicks elsewhere are ignored so they
            // don't paint, move the selection, or open menus.
            return;
        }

        if (rmb)
        {
            if (_drawing.Settings.Tool != ToolKind.None && _hasSelection && IsInsideSelection(pos))
            {
                _mode = InteractionMode.Erasing;
                RootGrid.CapturePointer(e.Pointer);
                TryEraseAt(pos);
                e.Handled = true;
            }
            return;
        }

        if (!lmb) return;

        if (_hasSelection && TryGetHandle(pos, out var hp))
        {
            _mode = InteractionMode.ResizingSelection;
            _activeHandle = hp;
            _selectionAtDragStart = _selectionRect;
            _dragStart = pos;
            RootGrid.CapturePointer(e.Pointer);
            return;
        }

        if (_hasSelection && IsInsideSelection(pos))
        {
            if (_drawing.Settings.Tool == ToolKind.None)
            {
                _mode = InteractionMode.MovingSelection;
                _selectionAtDragStart = _selectionRect;
                _dragStart = pos;
                RootGrid.CapturePointer(e.Pointer);
            }
            else
            {
                StartToolPress(pos, e.Pointer);
            }
            return;
        }

        // Outside or no selection: start new selection
        if (_drawing.Elements.Count > 0)
        {
            _drawing.ClearAll();
        }
        _mode = InteractionMode.SelectingNew;
        _hasSelection = false;
        _dragStart = pos;
        _selectionRect = new Rect(pos.X, pos.Y, 0, 0);
        UpdateSelectionVisual();
        Hint.Visibility = Visibility.Collapsed;
        RootGrid.CapturePointer(e.Pointer);
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(RootGrid).Position;
        if (_drawing.Settings.Tool == ToolKind.Pencil && _hasSelection && IsInsideSelection(pos))
        {
            _pencilPreview.Visibility = Visibility.Visible;
            var local = ToCanvas(pos);
            Canvas.SetLeft(_pencilPreview, local.X - _pencilPreview.Width / 2);
            Canvas.SetTop(_pencilPreview, local.Y - _pencilPreview.Height / 2);
        }
        else
        {
            _pencilPreview.Visibility = Visibility.Collapsed;
        }

        switch (_mode)
        {
            case InteractionMode.SelectingNew:
                _selectionRect = MakeRect(_dragStart, pos);
                UpdateSelectionVisual();
                break;
            case InteractionMode.MovingSelection:
            {
                double dx = pos.X - _dragStart.X;
                double dy = pos.Y - _dragStart.Y;
                _selectionRect = new Rect(
                    _selectionAtDragStart.X + dx,
                    _selectionAtDragStart.Y + dy,
                    _selectionAtDragStart.Width,
                    _selectionAtDragStart.Height);
                UpdateSelectionVisual();
                break;
            }
            case InteractionMode.ResizingSelection:
                _selectionRect = ResizeFromHandle(_selectionAtDragStart, _activeHandle, pos);
                UpdateSelectionVisual();
                break;
            case InteractionMode.DrawingStroke:
                ExtendStroke(pos);
                break;
            case InteractionMode.DrawingRect:
                UpdateActiveRect(pos);
                break;
            case InteractionMode.Erasing:
                TryEraseAt(pos);
                break;
            case InteractionMode.SelectingOcrText:
                UpdateOcrDragSelection(pos);
                break;
        }
    }

    private void OnRootPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(RootGrid).Position;
        RootGrid.ReleasePointerCapture(e.Pointer);

        switch (_mode)
        {
            case InteractionMode.SelectingNew:
            {
                var rect = MakeRect(_dragStart, pos);
                if (rect.Width < MinSelectionSize && rect.Height < MinSelectionSize)
                {
                    double x = _dragStart.X - SingleClickFallbackSize / 2;
                    double y = _dragStart.Y - SingleClickFallbackSize / 2;
                    rect = new Rect(x, y, SingleClickFallbackSize, SingleClickFallbackSize);
                }
                _selectionRect = rect;
                _hasSelection = true;
                UpdateSelectionVisual();
                ShowToolbars();
                break;
            }
            case InteractionMode.DrawingStroke:
                FinishStroke();
                break;
            case InteractionMode.DrawingRect:
                FinishActiveRect();
                break;
            case InteractionMode.SelectingOcrText:
                FinishOcrSelection(pos);
                break;
        }

        _mode = InteractionMode.Idle;
    }

    private void OnRootRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_inOcrMode)
        {
            // OCR mode owns right-click; never show the overlay context menu.
            e.Handled = true;
            return;
        }
        if (_drawing.Settings.Tool != ToolKind.None && _hasSelection)
        {
            var pos = e.GetPosition(RootGrid);
            if (IsInsideSelection(pos))
            {
                // RMB inside selection with tool active is erase, not menu.
                e.Handled = true;
                return;
            }
        }
        UpdateContextMenuVisibility();
    }

    // ---------- Selection / drawing helpers ----------

    private bool IsInsideSelection(Point p)
    {
        return _hasSelection
            && p.X >= _selectionRect.X && p.X <= _selectionRect.X + _selectionRect.Width
            && p.Y >= _selectionRect.Y && p.Y <= _selectionRect.Y + _selectionRect.Height;
    }

    private Point ToCanvas(Point root) => new(root.X - _selectionRect.X, root.Y - _selectionRect.Y);

    private static Rect MakeRect(Point a, Point b)
    {
        double x = System.Math.Min(a.X, b.X);
        double y = System.Math.Min(a.Y, b.Y);
        double w = System.Math.Abs(a.X - b.X);
        double h = System.Math.Abs(a.Y - b.Y);
        return new Rect(x, y, w, h);
    }

    private Rect ResizeFromHandle(Rect baseRect, HandlePos h, Point pos)
    {
        double left = baseRect.X, top = baseRect.Y, right = baseRect.X + baseRect.Width, bottom = baseRect.Y + baseRect.Height;
        switch (h)
        {
            case HandlePos.TL: left = pos.X; top = pos.Y; break;
            case HandlePos.T:  top = pos.Y; break;
            case HandlePos.TR: right = pos.X; top = pos.Y; break;
            case HandlePos.R:  right = pos.X; break;
            case HandlePos.BR: right = pos.X; bottom = pos.Y; break;
            case HandlePos.B:  bottom = pos.Y; break;
            case HandlePos.BL: left = pos.X; bottom = pos.Y; break;
            case HandlePos.L:  left = pos.X; break;
        }
        if (right < left) (left, right) = (right, left);
        if (bottom < top) (top, bottom) = (bottom, top);
        return new Rect(left, top, right - left, bottom - top);
    }

    private void UpdateSelectionVisual()
    {
        if (_selectionRect.Width <= 0 || _selectionRect.Height <= 0)
        {
            SelectionLayer.Visibility = Visibility.Collapsed;
            UpdateDimGeometry(null);
            PositionToolbars();
            return;
        }

        SelectionLayer.Visibility = Visibility.Visible;
        SelectionLayer.Margin = new Thickness(_selectionRect.X, _selectionRect.Y, 0, 0);
        SelectionLayer.Width = _selectionRect.Width;
        SelectionLayer.Height = _selectionRect.Height;

        SelectionBorder.Width = _selectionRect.Width;
        SelectionBorder.Height = _selectionRect.Height;
        HandlesLayer.Width = _selectionRect.Width;
        HandlesLayer.Height = _selectionRect.Height;
        CursorPreviewLayer.Width = _selectionRect.Width;
        CursorPreviewLayer.Height = _selectionRect.Height;

        PositionHandles();
        UpdateDimGeometry(_selectionRect);
        PositionToolbars();
    }

    private void UpdateDimGeometry(Rect? hole)
    {
        DimGeometry.Children.Clear();
        DimGeometry.Children.Add(new RectangleGeometry { Rect = new Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight) });
        if (hole.HasValue && hole.Value.Width > 0 && hole.Value.Height > 0)
        {
            DimGeometry.Children.Add(new RectangleGeometry { Rect = hole.Value });
        }
    }

    private void ShowToolbars()
    {
        BottomToolbar.Visibility = Visibility.Visible;
        RightToolbar.Visibility = Visibility.Visible;
        PositionToolbars();
    }

    private void HideToolbars()
    {
        BottomToolbar.Visibility = Visibility.Collapsed;
        RightToolbar.Visibility = Visibility.Collapsed;
    }

    private void PositionToolbars()
    {
        if (BottomToolbar.Visibility != Visibility.Visible || !_hasSelection) return;
        BottomToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        RightToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var rootW = RootGrid.ActualWidth;
        var rootH = RootGrid.ActualHeight;

        double bw = BottomToolbar.DesiredSize.Width;
        double bh = BottomToolbar.DesiredSize.Height;
        double rw = RightToolbar.DesiredSize.Width;
        double rh = RightToolbar.DesiredSize.Height;

        double bx = _selectionRect.X + (_selectionRect.Width - bw) / 2;
        double by = _selectionRect.Y + _selectionRect.Height + 12;
        if (by + bh > rootH - 8)
        {
            by = _selectionRect.Y - bh - 12;
            if (by < 8) by = _selectionRect.Y + _selectionRect.Height - bh - 8;
        }
        bx = System.Math.Clamp(bx, 8, System.Math.Max(8, rootW - bw - 8));
        Canvas.SetLeft(BottomToolbar, bx);
        Canvas.SetTop(BottomToolbar, by);

        double rx = _selectionRect.X + _selectionRect.Width + 12;
        double ry = _selectionRect.Y + (_selectionRect.Height - rh) / 2;
        if (rx + rw > rootW - 8)
        {
            rx = _selectionRect.X - rw - 12;
            if (rx < 8) rx = _selectionRect.X + _selectionRect.Width - rw - 8;
        }
        ry = System.Math.Clamp(ry, 8, System.Math.Max(8, rootH - rh - 8));
        Canvas.SetLeft(RightToolbar, rx);
        Canvas.SetTop(RightToolbar, ry);
    }

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
            case ToolKind.Text:
                // Click-to-place: do not enter a drag mode and do not capture
                // the pointer. PointerReleased resets _mode to Idle, and the
                // TextBox keeps focus until LostFocus / Enter / Esc.
                StartTextEntry(pos);
                break;
        }
    }

    private void ExtendStroke(Point pos)
    {
        if (_activeStroke == null || _activeStrokeVisual == null) return;
        _activeStroke.Points.Add(pos);
        _activeStrokeVisual.Points.Add(pos);
    }

    private void FinishStroke()
    {
        if (_activeStroke == null || _activeStrokeVisual == null) return;
        DrawingCanvas.Children.Remove(_activeStrokeVisual);
        _drawing.Add(_activeStroke);
        _activeStroke = null;
        _activeStrokeVisual = null;
    }

    private void UpdateActiveRect(Point pos)
    {
        if (_activeRectVisual == null) return;
        double x = System.Math.Min(_activeRectAnchor.X, pos.X);
        double y = System.Math.Min(_activeRectAnchor.Y, pos.Y);
        double w = System.Math.Abs(pos.X - _activeRectAnchor.X);
        double h = System.Math.Abs(pos.Y - _activeRectAnchor.Y);
        Canvas.SetLeft(_activeRectVisual, x);
        Canvas.SetTop(_activeRectVisual, y);
        _activeRectVisual.Width = w;
        _activeRectVisual.Height = h;
    }

    private void FinishActiveRect()
    {
        if (_activeRectVisual == null) return;
        double x = Canvas.GetLeft(_activeRectVisual);
        double y = Canvas.GetTop(_activeRectVisual);
        double w = _activeRectVisual.Width;
        double h = _activeRectVisual.Height;
        DrawingCanvas.Children.Remove(_activeRectVisual);
        if (w < 2 || h < 2) { _activeRectVisual = null; return; }
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
        _activeRectVisual = null;
    }

    private void StartTextEntry(Point pos)
    {
        // Commit any prior entry before opening a new one.
        if (_activeTextBox != null) CommitText();

        var tb = new TextBox
        {
            MinWidth = 80,
            AcceptsReturn = false,
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            Foreground = new SolidColorBrush(_drawing.Settings.Color),
            BorderBrush = new SolidColorBrush(_drawing.Settings.Color),
            BorderThickness = new Thickness(1),
            FontSize = _drawing.Settings.TextSize,
            Padding = new Thickness(4, 2, 4, 2),
        };
        Canvas.SetLeft(tb, pos.X);
        Canvas.SetTop(tb, pos.Y);
        DrawingCanvas.Children.Add(tb);
        _activeTextBox = tb;
        tb.LostFocus += (_, _) => CommitText();
        tb.KeyDown += (_, ke) =>
        {
            if (ke.Key == VirtualKey.Enter) { ke.Handled = true; CommitText(); }
            else if (ke.Key == VirtualKey.Escape) { ke.Handled = true; CancelText(); }
        };
        // Eat pointer events so RootGrid handlers don't re-trigger StartToolPress
        // when the user clicks inside the active text box.
        tb.PointerPressed += (_, ev) => ev.Handled = true;
        tb.PointerReleased += (_, ev) => ev.Handled = true;
        DrawingCanvas.IsHitTestVisible = true;
        tb.Focus(FocusState.Programmatic);
    }

    private void CommitText()
    {
        if (_activeTextBox == null) return;
        var text = _activeTextBox.Text ?? string.Empty;
        double x = Canvas.GetLeft(_activeTextBox);
        double y = Canvas.GetTop(_activeTextBox);
        var owning = _activeTextBox;
        _activeTextBox = null;
        DrawingCanvas.Children.Remove(owning);
        DrawingCanvas.IsHitTestVisible = false;
        if (string.IsNullOrWhiteSpace(text)) return;
        var tb = new TextBlock
        {
            Text = text,
            FontSize = _drawing.Settings.TextSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_drawing.Settings.Color),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = tb.DesiredSize;
        var element = new TextElement
        {
            Visual = tb,
            Position = new Point(x, y),
            Text = text,
            FontSize = _drawing.Settings.TextSize,
            MeasuredSize = size,
        };
        _drawing.Add(element);
    }

    private void CancelText()
    {
        if (_activeTextBox == null) return;
        DrawingCanvas.Children.Remove(_activeTextBox);
        _activeTextBox = null;
        DrawingCanvas.IsHitTestVisible = false;
    }

    private void TryEraseAt(Point rootPos)
    {
        var hit = _drawing.HitTestTopmost(rootPos, EraserRadius);
        if (hit != null) _drawing.Remove(hit);
    }

    // ---------- Keyboard ----------

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsCtrlDown();
        if (_activeTextBox != null) return; // typing in textbox; handled by it

        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                HandleEscape();
                return;
            case VirtualKey.A when ctrl:
                e.Handled = true;
                SelectAll();
                return;
            case VirtualKey.Z when ctrl:
                e.Handled = true;
                _drawing.Undo();
                return;
            case VirtualKey.Y when ctrl:
                e.Handled = true;
                _drawing.Redo();
                return;
            case VirtualKey.S when ctrl:
                if (_inOcrMode) return; // do not steal save during OCR
                e.Handled = true;
                _ = SaveSilentAsync();
                return;
            case VirtualKey.C when ctrl:
                e.Handled = true;
                if (_inOcrMode) { _ = CopyOcrTextAsync(); return; }
                _ = CopyAsync();
                return;
        }
    }

    private void HandleEscape()
    {
        if (_inOcrMode)
        {
            ExitOcrMode();
            return;
        }
        if (_drawing.Settings.Tool != ToolKind.None)
        {
            SetTool(ToolKind.None);
            return;
        }
        Close();
    }

    private static bool IsCtrlDown()
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    private void SelectAll()
    {
        var rect = new Rect(0, 0, RootGrid.ActualWidth, RootGrid.ActualHeight);
        SetSelection(rect);
    }

    private void SetSelection(Rect rect)
    {
        _selectionRect = rect;
        _hasSelection = true;
        Hint.Visibility = Visibility.Collapsed;
        UpdateSelectionVisual();
        ShowToolbars();
    }

    // ---------- Toolbar / tool selection ----------

    private void OnToolToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton tb) return;
        ToolKind tool = tb.Name switch
        {
            "PencilBtn" => ToolKind.Pencil,
            "RectBtn" => ToolKind.Rectangle,
            "TextBtn" => ToolKind.Text,
            _ => ToolKind.None,
        };
        SetTool(tb.IsChecked == true ? tool : ToolKind.None);
    }

    private void SetTool(ToolKind tool)
    {
        _drawing.Settings.Tool = tool;
        PencilBtn.IsChecked = tool == ToolKind.Pencil;
        RectBtn.IsChecked = tool == ToolKind.Rectangle;
        TextBtn.IsChecked = tool == ToolKind.Text;
        if (tool != ToolKind.Pencil) _pencilPreview.Visibility = Visibility.Collapsed;
    }

    private void OnColorPick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem mfi && mfi.Tag is string hex)
        {
            var color = ParseHexColor(hex);
            _drawing.Settings.Color = color;
            ColorSwatch.Fill = new SolidColorBrush(color);
        }
    }

    private static Color ParseHexColor(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 8)
        {
            byte a = System.Convert.ToByte(s.Substring(0, 2), 16);
            byte r = System.Convert.ToByte(s.Substring(2, 2), 16);
            byte g = System.Convert.ToByte(s.Substring(4, 2), 16);
            byte b = System.Convert.ToByte(s.Substring(6, 2), 16);
            return Color.FromArgb(a, r, g, b);
        }
        if (s.Length == 6)
        {
            byte r = System.Convert.ToByte(s.Substring(0, 2), 16);
            byte g = System.Convert.ToByte(s.Substring(2, 2), 16);
            byte b = System.Convert.ToByte(s.Substring(4, 2), 16);
            return Color.FromArgb(0xFF, r, g, b);
        }
        return Microsoft.UI.Colors.Red;
    }

    // ---------- Bottom toolbar actions ----------

    private async void OnRecordClick(object sender, RoutedEventArgs e)
    {
        if (!_hasSelection) return;
        var scale = DpiScale;
        var b = _frame.VirtualBounds;
        int x = b.X + (int)System.Math.Round(_selectionRect.X * scale);
        int y = b.Y + (int)System.Math.Round(_selectionRect.Y * scale);
        int w = (int)System.Math.Round(_selectionRect.Width * scale);
        int h = (int)System.Math.Round(_selectionRect.Height * scale);
        if (w < 8 || h < 8) return;
        var dq = App.Current.HostWindow!.DispatcherQueue;
        Close();
        await Task.Delay(150);
        dq.TryEnqueue(() => RecordingController.TryStart(x, y, w, h));
    }
    private void OnScreenshotClick(object sender, RoutedEventArgs e) => _ = SaveAsAsync();
    private void OnCopyClick(object sender, RoutedEventArgs e) => _ = CopyAsync();
    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
    private async void OnOcrClick(object sender, RoutedEventArgs e) => await EnterOcrModeAsync();

    // ---------- Screenshot save / copy ----------

    private double DpiScale => Content?.XamlRoot?.RasterizationScale ?? 1.0;

    private async Task SaveSilentAsync()
    {
        if (!_hasSelection) return;
        try
        {
            var settings = SettingsService.Instance;
            var folder = settings.GetEffectiveScreenshotFolder();
            Directory.CreateDirectory(folder);
            var name = SaveDialogService.MakeTimestampName("Clipsy", "png");
            var fullPath = IOPath.Combine(folder, name);
            var png = ScreenshotRenderer.RenderPng(_frame, _selectionRect, _drawing.Elements, DpiScale);
            await File.WriteAllBytesAsync(fullPath, png);
            Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Silent save failed: {ex.Message}");
            NotificationService.Error("ErrSaveFailed");
        }
    }

    private async Task SaveAsAsync()
    {
        if (!_hasSelection) return;
        try
        {
            var settings = SettingsService.Instance;
            var suggestedFolder = settings.GetEffectiveScreenshotFolder();
            var name = SaveDialogService.MakeTimestampName("Clipsy", "png");
            var result = await SaveDialogService.PickPngSaveAsync(_hwnd, suggestedFolder, name);
            if (result == null) return;
            var png = ScreenshotRenderer.RenderPng(_frame, _selectionRect, _drawing.Elements, DpiScale);
            await File.WriteAllBytesAsync(result.Path, png);
            var dir = IOPath.GetDirectoryName(result.Path);
            if (!string.IsNullOrEmpty(dir))
            {
                settings.Settings.LastScreenshotFolder = dir;
                settings.Save();
            }
            Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Save As failed: {ex.Message}");
            NotificationService.Error("ErrSaveFailed");
        }
    }

    private async Task CopyAsync()
    {
        if (!_hasSelection) return;
        try
        {
            var png = ScreenshotRenderer.RenderPng(_frame, _selectionRect, _drawing.Elements, DpiScale);
            await ClipboardService.SetImageAsync(png);
            Close();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Copy failed: {ex.Message}");
            NotificationService.Error("ErrCopyFailed");
        }
    }

    // ---------- Context menu ----------

    private void BuildScreenMenu()
    {
        SelectScreenMenu.Items.Clear();
        int i = 1;
        foreach (var m in _frame.Monitors)
        {
            var item = new MenuFlyoutItem
            {
                Text = $"Screen {i}" + (m.IsPrimary ? " (primary)" : string.Empty),
                Tag = m,
            };
            item.Click += OnMenuSelectScreen;
            SelectScreenMenu.Items.Add(item);
            i++;
        }
    }

    private void UpdateContextMenuVisibility()
    {
        bool s = _hasSelection;
        var vis = s ? Visibility.Visible : Visibility.Collapsed;
        SelectionMenuSeparator.Visibility = vis;
        MenuCopy.Visibility = vis;
        MenuSave.Visibility = vis;
        MenuSaveAs.Visibility = vis;
        MenuClear.Visibility = vis;
    }

    private void OnMenuSelectScreen(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem mfi && mfi.Tag is ScreenFreezeService.MonitorInfo m)
        {
            var b = _frame.VirtualBounds;
            var rect = new Rect(m.Bounds.X - b.X, m.Bounds.Y - b.Y, m.Bounds.Width, m.Bounds.Height);
            SetSelection(rect);
        }
    }

    private void OnMenuSelectAll(object sender, RoutedEventArgs e) => SelectAll();
    private void OnMenuCopy(object sender, RoutedEventArgs e) => _ = CopyAsync();
    private void OnMenuSave(object sender, RoutedEventArgs e) => _ = SaveSilentAsync();
    private void OnMenuSaveAs(object sender, RoutedEventArgs e) => _ = SaveAsAsync();
    private void OnMenuClear(object sender, RoutedEventArgs e)
    {
        _drawing.ClearAll();
        _hasSelection = false;
        SelectionLayer.Visibility = Visibility.Collapsed;
        HideToolbars();
        UpdateDimGeometry(null);
        Hint.Visibility = Visibility.Visible;
    }
    private void OnMenuCancel(object sender, RoutedEventArgs e) => Close();

    // ---------- OCR ----------

    private async Task EnterOcrModeAsync()
    {
        if (!_hasSelection || _inOcrMode) return;
        _inOcrMode = true;
        SetTool(ToolKind.None);
        BottomToolbar.Visibility = Visibility.Collapsed;
        // Right toolbar stays visible during scan but its tools become
        // unusable so the user can't paint on top of the OCR overlay.
        SetRightToolbarEnabled(false);
        TranslatePanel.Visibility = Visibility.Collapsed;
        OcrTextPanel.Visibility = Visibility.Collapsed;
        ClearOcrVisuals();
        OcrStatusLabel.Visibility = Visibility.Collapsed;
        OcrLayer.Visibility = Visibility.Visible;
        OcrToolbar.Visibility = Visibility.Visible;
        SetOcrButtonsEnabled(false); // disabled until results come back
        PositionOcrToolbar();
        StartScanAnimation();

        IReadOnlyList<OcrWord> words;
        try
        {
            var png = ScreenshotRenderer.RenderPng(_frame, _selectionRect, Array.Empty<DrawElement>(), DpiScale);
            var engine = OcrEngineFactory.Resolve();
            words = await engine.RecognizeAsync(png);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] OCR failed: {ex.Message}");
            NotificationService.Error("ErrOcrFailed");
            words = Array.Empty<OcrWord>();
        }

        StopScanAnimation();
        if (!_inOcrMode) return;
        RenderOcrResults(words);
    }

    private void ExitOcrMode()
    {
        _inOcrMode = false;
        StopScanAnimation();
        OcrLayer.Visibility = Visibility.Collapsed;
        ClearOcrVisuals();
        OcrToolbar.Visibility = Visibility.Collapsed;
        TranslatePanel.Visibility = Visibility.Collapsed;
        OcrTextPanel.Visibility = Visibility.Collapsed;
        OcrStatusLabel.Visibility = Visibility.Collapsed;
        OcrTextBox.Text = string.Empty;
        SetRightToolbarEnabled(true);
        if (_hasSelection)
        {
            BottomToolbar.Visibility = Visibility.Visible;
            RightToolbar.Visibility = Visibility.Visible;
        }
    }

    private void SetRightToolbarEnabled(bool enabled)
    {
        PencilBtn.IsEnabled = enabled;
        RectBtn.IsEnabled = enabled;
        TextBtn.IsEnabled = enabled;
        ColorBtn.IsEnabled = enabled;
        OcrBtn.IsEnabled = enabled;
    }

    private void SetOcrButtonsEnabled(bool enabled)
    {
        OcrSelectAllBtn.IsEnabled = enabled;
        OcrCopyBtn.IsEnabled = enabled;
        OcrTranslateBtn.IsEnabled = enabled;
        OcrExitBtn.IsEnabled = true; // always reachable
    }

    private void StartScanAnimation()
    {
        // ScanLine lives inside SelectionLayer (which Margin-positions to the
        // selection). Width tracks the selection's local size.
        ScanLine.Visibility = Visibility.Visible;
        ScanLine.Width = _selectionRect.Width;
        Canvas.SetLeft(ScanLine, 0);
        Canvas.SetTop(ScanLine, 0);
        _scanY = 0;
        _scanDir = 1;
        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scanTimer.Tick += OnScanTick;
        _scanTimer.Start();
    }

    private void OnScanTick(object? sender, object e)
    {
        _scanY += _scanDir * 6;
        if (_scanY >= _selectionRect.Height) { _scanY = _selectionRect.Height; _scanDir = -1; }
        if (_scanY <= 0) { _scanY = 0; _scanDir = 1; }
        Canvas.SetTop(ScanLine, _scanY);
    }

    private void StopScanAnimation()
    {
        if (_scanTimer != null)
        {
            _scanTimer.Stop();
            _scanTimer.Tick -= OnScanTick;
            _scanTimer = null;
        }
        ScanLine.Visibility = Visibility.Collapsed;
    }

    private void ClearOcrVisuals()
    {
        foreach (var (_, box, glyph) in _ocrVisuals)
        {
            OcrLayer.Children.Remove(box);
            OcrLayer.Children.Remove(glyph);
        }
        _ocrVisuals.Clear();
        _ocrWordsRaw.Clear();
        _ocrWordsDip.Clear();
        _ocrSelected.Clear();
    }

    private void RenderOcrResults(IReadOnlyList<OcrWord> words)
    {
        if (words.Count == 0)
        {
            OcrStatusLabel.Text = Strings.Get("NoTextFound");
            Canvas.SetLeft(OcrStatusLabel, System.Math.Max(8, _selectionRect.Width / 2 - 50));
            Canvas.SetTop(OcrStatusLabel, System.Math.Max(8, _selectionRect.Height / 2 - 10));
            OcrStatusLabel.Visibility = Visibility.Visible;
            _ = FadeOutLaterAsync(OcrStatusLabel, 2500);
            SetOcrButtonsEnabled(false);
            return;
        }

        // Map word bounds from source-bitmap pixels to root DIPs:
        //   root = selection origin + (pixel / dpiScale)
        var scale = DpiScale;
        var ox = _selectionRect.X;
        var oy = _selectionRect.Y;

        foreach (var w in words)
        {
            _ocrWordsRaw.Add(w);
            var b = new Rect(
                ox + w.BoundsPixels.X / scale,
                oy + w.BoundsPixels.Y / scale,
                w.BoundsPixels.Width / scale,
                w.BoundsPixels.Height / scale);
            _ocrWordsDip.Add(b);

            var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Width = b.Width,
                Height = b.Height,
                Fill = new SolidColorBrush(Color.FromArgb(80, 0xFF, 0xEB, 0x3B)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(rect, b.X);
            Canvas.SetTop(rect, b.Y);
            OcrLayer.Children.Add(rect);

            _ocrVisuals.Add((b, rect, new TextBlock()));
        }

        // Build sorted, line-grouped text for the text panel.
        OcrTextBox.Text = BuildSortedText();
        OcrTextPanel.Visibility = Visibility.Visible;
        PositionOcrTextPanel();
        SetOcrButtonsEnabled(true);
    }

    private string BuildSortedText()
    {
        if (_ocrWordsDip.Count == 0) return string.Empty;
        // Estimate a typical line height to group words into rows.
        double medianH = _ocrWordsDip.Select(r => r.Height).OrderBy(h => h)
            .ElementAt(_ocrWordsDip.Count / 2);
        double lineTolerance = System.Math.Max(4, medianH * 0.55);

        // Pair indices with bounds and sort by Y, then X.
        var sorted = Enumerable.Range(0, _ocrWordsDip.Count)
            .OrderBy(i => _ocrWordsDip[i].Y)
            .ThenBy(i => _ocrWordsDip[i].X)
            .ToList();

        var sb = new StringBuilder();
        double currentLineY = double.NaN;
        var lineBuffer = new List<int>();

        void Flush()
        {
            if (lineBuffer.Count == 0) return;
            lineBuffer.Sort((a, b) => _ocrWordsDip[a].X.CompareTo(_ocrWordsDip[b].X));
            for (int k = 0; k < lineBuffer.Count; k++)
            {
                if (k > 0) sb.Append(' ');
                sb.Append(_ocrWordsRaw[lineBuffer[k]].Text);
            }
            sb.AppendLine();
            lineBuffer.Clear();
        }

        foreach (var i in sorted)
        {
            var y = _ocrWordsDip[i].Y;
            if (double.IsNaN(currentLineY)) currentLineY = y;
            if (System.Math.Abs(y - currentLineY) > lineTolerance)
            {
                Flush();
                currentLineY = y;
            }
            lineBuffer.Add(i);
        }
        Flush();
        return sb.ToString().TrimEnd();
    }

    private void PositionOcrTextPanel()
    {
        OcrTextPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = OcrTextPanel.DesiredSize;
        double rootW = RootGrid.ActualWidth;
        double rootH = RootGrid.ActualHeight;
        double tx = Canvas.GetLeft(OcrToolbar);
        double ty = Canvas.GetTop(OcrToolbar) + OcrToolbar.DesiredSize.Height + 8;
        if (ty + sz.Height > rootH - 8)
        {
            ty = _selectionRect.Y - sz.Height - 12;
            if (ty < 8) ty = 8;
        }
        if (tx + sz.Width > rootW - 8) tx = rootW - sz.Width - 8;
        if (tx < 8) tx = 8;
        Canvas.SetLeft(OcrTextPanel, tx);
        Canvas.SetTop(OcrTextPanel, ty);
    }

    private void UpdateOcrDragSelection(Point pos)
    {
        var dragRoot = MakeRect(_ocrDragStart, pos);
        var dragLocal = new Rect(
            dragRoot.X - _selectionRect.X,
            dragRoot.Y - _selectionRect.Y,
            dragRoot.Width,
            dragRoot.Height);
        _ocrSelected.Clear();
        for (int i = 0; i < _ocrWordsDip.Count; i++)
        {
            if (RectsIntersect(_ocrWordsDip[i], dragLocal)) _ocrSelected.Add(i);
        }
        UpdateOcrSelectionVisual();
    }

    private void FinishOcrSelection(Point pos)
    {
        var dragRoot = MakeRect(_ocrDragStart, pos);
        if (dragRoot.Width < 4 && dragRoot.Height < 4)
        {
            var local = new Point(_ocrDragStart.X - _selectionRect.X, _ocrDragStart.Y - _selectionRect.Y);
            int idx = -1;
            for (int i = 0; i < _ocrWordsDip.Count; i++)
            {
                var b = _ocrWordsDip[i];
                if (local.X >= b.X && local.X <= b.X + b.Width && local.Y >= b.Y && local.Y <= b.Y + b.Height)
                {
                    idx = i;
                    break;
                }
            }
            _ocrSelected.Clear();
            if (idx >= 0) _ocrSelected.Add(idx);
            UpdateOcrSelectionVisual();
        }
    }

    private static bool RectsIntersect(Rect a, Rect b)
    {
        return !(b.X > a.X + a.Width || b.X + b.Width < a.X || b.Y > a.Y + a.Height || b.Y + b.Height < a.Y);
    }

    private void UpdateOcrSelectionVisual()
    {
        var unsel = new SolidColorBrush(Color.FromArgb(80, 0xFF, 0xEB, 0x3B));
        var sel = new SolidColorBrush(Color.FromArgb(170, 0xFF, 0xEB, 0x3B));
        for (int i = 0; i < _ocrVisuals.Count; i++)
        {
            _ocrVisuals[i].box.Fill = _ocrSelected.Contains(i) ? sel : unsel;
        }
    }

    private string GetSelectedOcrText()
    {
        if (_ocrSelected.Count == 0) return string.Empty;
        var sorted = _ocrSelected
            .OrderBy(i => _ocrWordsDip[i].Y)
            .ThenBy(i => _ocrWordsDip[i].X)
            .ToList();
        var sb = new StringBuilder();
        double lastY = double.NaN;
        double lastH = 0;
        foreach (var i in sorted)
        {
            var b = _ocrWordsDip[i];
            if (!double.IsNaN(lastY) && System.Math.Abs(b.Y - lastY) > lastH * 0.6)
            {
                sb.AppendLine();
            }
            else if (sb.Length > 0)
            {
                sb.Append(' ');
            }
            sb.Append(_ocrWordsRaw[i].Text);
            lastY = b.Y;
            lastH = b.Height;
        }
        return sb.ToString();
    }

    private void PositionOcrToolbar()
    {
        OcrToolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var rootW = RootGrid.ActualWidth;
        var rootH = RootGrid.ActualHeight;
        double w = OcrToolbar.DesiredSize.Width;
        double h = OcrToolbar.DesiredSize.Height;
        double x = _selectionRect.X + (_selectionRect.Width - w) / 2;
        double y = _selectionRect.Y + _selectionRect.Height + 12;
        if (y + h > rootH - 8) y = _selectionRect.Y - h - 12;
        x = System.Math.Clamp(x, 8, System.Math.Max(8, rootW - w - 8));
        Canvas.SetLeft(OcrToolbar, x);
        Canvas.SetTop(OcrToolbar, y);
    }

    private void PositionTranslatePanel()
    {
        TranslatePanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var rootW = RootGrid.ActualWidth;
        var rootH = RootGrid.ActualHeight;
        double w = TranslatePanel.DesiredSize.Width;
        double h = TranslatePanel.DesiredSize.Height;
        double x = _selectionRect.X + (_selectionRect.Width - w) / 2;
        double y = Canvas.GetTop(OcrToolbar) + OcrToolbar.DesiredSize.Height + 8;
        if (y + h > rootH - 8) y = _selectionRect.Y - h - 12;
        x = System.Math.Clamp(x, 8, System.Math.Max(8, rootW - w - 8));
        Canvas.SetLeft(TranslatePanel, x);
        Canvas.SetTop(TranslatePanel, y);
    }

    private void OnOcrSelectAll(object sender, RoutedEventArgs e)
    {
        OcrTextBox.Focus(FocusState.Programmatic);
        OcrTextBox.SelectAll();
    }

    private async void OnOcrCopy(object sender, RoutedEventArgs e)
    {
        await CopyOcrTextAsync();
    }

    private async Task CopyOcrTextAsync()
    {
        string text = OcrTextBox.SelectionLength > 0
            ? OcrTextBox.SelectedText
            : OcrTextBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] OCR copy failed: {ex.Message}");
            return;
        }
        OcrStatusLabel.Text = Strings.Get("Copied");
        Canvas.SetLeft(OcrStatusLabel, System.Math.Max(8, _selectionRect.Width / 2 - 30));
        Canvas.SetTop(OcrStatusLabel, 8);
        OcrStatusLabel.Visibility = Visibility.Visible;
        await FadeOutLaterAsync(OcrStatusLabel, 1200);
    }

    private async void OnOcrTranslate(object sender, RoutedEventArgs e)
    {
        string text = OcrTextBox.SelectionLength > 0
            ? OcrTextBox.SelectedText
            : OcrTextBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        TranslateOriginal.Text = text;
        TranslateTarget.Text = "...";
        TranslatePanel.Visibility = Visibility.Visible;
        PositionTranslatePanel();
        var (from, to) = TranslationService.GuessLangPair(text);
        var translated = await TranslationService.TranslateAsync(text, from, to);
        TranslateTarget.Text = translated ?? Strings.Get("TranslateUnavailable");
        PositionTranslatePanel();
    }

    private void OnOcrExit(object sender, RoutedEventArgs e) => ExitOcrMode();

    private async Task FadeOutLaterAsync(UIElement el, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs);
            el.Visibility = Visibility.Collapsed;
        }
        catch { /* ignore */ }
    }
}
