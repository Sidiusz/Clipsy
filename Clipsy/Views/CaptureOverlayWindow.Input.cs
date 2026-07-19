using Clipsy.Drawing;
using Clipsy.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;

namespace Clipsy.Views;

public sealed partial class CaptureOverlayWindow
{
    // ---------- Pointer input ----------

    private long _lastClickTick;
    private Point _lastClickPos;
    private bool _selectionFromFallback;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var cp = e.GetCurrentPoint(RootGrid);
        var pos = cp.Position;
        bool rmb = cp.Properties.IsRightButtonPressed;
        bool lmb = cp.Properties.IsLeftButtonPressed;

        if (_eyedropperActive)
        {
            if (lmb)
                ApplyPickedColor(SamplePixel(pos));
            if (_tempEyedropper) ExitTempEyedropper(pick: false);
            else ExitEyedropperMode();
            e.Handled = true;
            return;
        }

        if (_inOcrMode)
        {
            // OCR mode owns the overlay (selection is inside OcrTextBox); ignore
            // clicks elsewhere so they don't paint, move, or open menus.
            return;
        }

        // With a paint tool active the selection is locked: clicks anywhere paint
        // (LMB) or erase (RMB), so drawing outside the rect won't start a new one.
        if (_drawing.Settings.Tool != ToolKind.None)
        {
            if (rmb)
            {
                _mode = InteractionMode.Erasing;
                RootGrid.CapturePointer(e.Pointer);
                TryEraseAt(pos);
                e.Handled = true;
                return;
            }
            if (lmb)
            {
                StartToolPress(pos, e.Pointer);
                e.Handled = true;
                return;
            }
            return;
        }

        if (rmb) return; // let RightTapped surface the overlay context menu

        if (!lmb) return;

        // Double-click snaps to the monitor under the cursor; detected manually
        // and checked first (the first click drops a fallback selection).
        long nowTick = Environment.TickCount64;
        bool isDouble = nowTick - _lastClickTick <= GetDoubleClickTime()
            && System.Math.Abs(pos.X - _lastClickPos.X) < 8
            && System.Math.Abs(pos.Y - _lastClickPos.Y) < 8;
        _lastClickTick = nowTick;
        _lastClickPos = pos;
        if (isDouble)
        {
            _lastClickTick = 0; // consume so a triple-click doesn't re-trigger
            // Grab a committed text element to reposition it (no tool active).
            if (TryGrabText(pos, e.Pointer))
            {
                e.Handled = true;
                return;
            }
            if (TrySelectMonitorAt(pos))
            {
                e.Handled = true;
                return;
            }
        }

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
            _mode = InteractionMode.MovingSelection;
            _selectionAtDragStart = _selectionRect;
            _dragStart = pos;
            RootGrid.CapturePointer(e.Pointer);
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
        // Old islands must not linger frozen in place while a new region is dragged.
        HideToolbars();
        UpdateSelectionVisual();
        Hint.Visibility = Visibility.Collapsed;
        RootGrid.CapturePointer(e.Pointer);
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(RootGrid).Position;
        _lastPointerPos = pos;

        // Hold the configured modifier over a draw tool to temporarily eyedrop;
        // releasing it (or clicking) picks the colour and returns to drawing.
        if (!_inOcrMode && _drawing.Settings.Tool != ToolKind.None && _activeTextBox == null)
        {
            bool mod = IsEyedropperModifierDown();
            if (mod && !_tempEyedropper && !_eyedropperActive) EnterTempEyedropper();
            else if (!mod && _tempEyedropper) { ExitTempEyedropper(pick: true); return; }
        }

        if (_eyedropperActive)
        {
            UpdateMagnifier(pos);
            return;
        }
        var local = new Point(pos.X - _selectionRect.X, pos.Y - _selectionRect.Y);
        if (_drawing.Settings.Tool is ToolKind.Pencil or ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Line or ToolKind.Arrow)
        {
            _pencilPreview.Visibility = Visibility.Visible;
            _textPreview.Visibility = Visibility.Collapsed;
            Canvas.SetLeft(_pencilPreview, local.X - _pencilPreview.Width / 2);
            Canvas.SetTop(_pencilPreview, local.Y - _pencilPreview.Height / 2);
        }
        else if (_drawing.Settings.Tool == ToolKind.Text && _activeTextBox == null)
        {
            _pencilPreview.Visibility = Visibility.Collapsed;
            _textPreview.Visibility = Visibility.Visible;
            _textPreview.FontSize = _drawing.Settings.TextSize;
            // Mirror the font and center the glyph on the cursor, matching
            // StartTextEntry's anchor so the preview lands where the text will.
            try { _textPreview.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(_drawing.Settings.TextFont); }
            catch { /* fallback to inherited font */ }
            var (pw, ph) = MeasureGlyph(_textPreview.Text, _textPreview.FontSize, _textPreview.FontFamily);
            Canvas.SetLeft(_textPreview, local.X - pw / 2);
            Canvas.SetTop(_textPreview,  local.Y - ph / 2);
        }
        else
        {
            _pencilPreview.Visibility = Visibility.Collapsed;
            _textPreview.Visibility = Visibility.Collapsed;
        }

        switch (_mode)
        {
            case InteractionMode.SelectingNew:
                _selectionRect = MakeRect(_dragStart, pos);
                // Coalesce to one visual update per composition frame, else
                // high-polling mice trigger dozens of layout passes per frame.
                RequestSelectionVisualUpdate();
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
                RequestSelectionVisualUpdate();
                break;
            }
            case InteractionMode.ResizingSelection:
                _selectionRect = ResizeFromHandle(_selectionAtDragStart, _activeHandle, pos);
                RequestSelectionVisualUpdate();
                break;
            case InteractionMode.DrawingStroke:
                // GetIntermediatePoints returns all high-frequency samples buffered between
                // PointerMoved events — critical for smooth strokes at 144Hz+.
                var pts = e.GetIntermediatePoints(RootGrid);
                if (pts != null && pts.Count > 0)
                    foreach (var p in pts) ExtendStroke(p.Position);
                else
                    ExtendStroke(pos);
                break;
            case InteractionMode.DrawingRect:
                UpdateActiveShape(pos);
                break;
            case InteractionMode.Erasing:
                TryEraseAt(pos);
                break;
            case InteractionMode.SelectingOcrText:
                UpdateOcrDragSelection(pos);
                break;
            case InteractionMode.MovingText:
                if (_movingText != null)
                {
                    double nx = pos.X - _movingTextGrab.X;
                    double ny = pos.Y - _movingTextGrab.Y;
                    _movingText.Position = new Point(nx, ny);
                    Canvas.SetLeft(_movingText.Visual, nx);
                    Canvas.SetTop(_movingText.Visual, ny);
                }
                break;
        }
    }

    // ---------- Move committed text ----------

    private TextElement? _movingText;
    private Point _movingTextGrab;

    private bool TryGrabText(Point pos, Pointer pointer)
    {
        if (_drawing.Settings.Tool != ToolKind.None) return false;
        for (int i = _drawing.Elements.Count - 1; i >= 0; i--)
        {
            if (_drawing.Elements[i] is TextElement te && te.HitTest(pos, 4))
            {
                _mode = InteractionMode.MovingText;
                _movingText = te;
                _movingTextGrab = new Point(pos.X - te.Position.X, pos.Y - te.Position.Y);
                RootGrid.CapturePointer(pointer);
                return true;
            }
        }
        return false;
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
                    _selectionFromFallback = true; // a double-click may replace it with a full monitor
                }
                else _selectionFromFallback = false;
                _selectionRect = rect;
                _hasSelection = true;
                // Dynamic islands anchor to the corner where the drag ended.
                _anchorRight = pos.X >= _dragStart.X;
                _anchorBottom = pos.Y >= _dragStart.Y;
                UpdateSelectionVisual();
                ShowToolbars();
                break;
            }
            case InteractionMode.MovingSelection:
                UpdateSelectionVisual();
                break;
            case InteractionMode.ResizingSelection:
                // Re-anchor to the dragged handle's corner/edge.
                switch (_activeHandle)
                {
                    case HandlePos.TL: _anchorRight = false; _anchorBottom = false; break;
                    case HandlePos.T:  _anchorBottom = false; break;
                    case HandlePos.TR: _anchorRight = true;  _anchorBottom = false; break;
                    case HandlePos.R:  _anchorRight = true;  break;
                    case HandlePos.BR: _anchorRight = true;  _anchorBottom = true;  break;
                    case HandlePos.B:  _anchorBottom = true; break;
                    case HandlePos.BL: _anchorRight = false; _anchorBottom = true;  break;
                    case HandlePos.L:  _anchorRight = false; break;
                }
                UpdateSelectionVisual();
                break;
            case InteractionMode.DrawingStroke:
                FinishStroke();
                break;
            case InteractionMode.DrawingRect:
                FinishActiveShape();
                break;
            case InteractionMode.SelectingOcrText:
                FinishOcrSelection(pos);
                break;
            case InteractionMode.MovingText:
                _movingText = null;
                break;
        }

        _mode = InteractionMode.Idle;
    }

    private void OnRootPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!_hasSelection) return;
        int delta = e.GetCurrentPoint(RootGrid).Properties.MouseWheelDelta;
        if (delta == 0) return;
        double step = delta > 0 ? 1.0 : -1.0;

        // Wheel while typing resizes the active text element instead of the
        // brush — same gesture, meaning depends on what the user is doing.
        if (_activeTextBox != null)
        {
            _drawing.Settings.BrushSize = System.Math.Clamp(_drawing.Settings.BrushSize + step, 1.0, 64.0);
            _activeTextBox.FontSize = _drawing.Settings.TextSize;
            e.Handled = true;
            return;
        }

        _drawing.Settings.BrushSize = System.Math.Clamp(_drawing.Settings.BrushSize + step, 1.0, 64.0);
        UpdatePreviewForThickness(_drawing.Settings.BrushSize);

        // Refresh the text-tool preview live too — wheeling between letters
        // shouldn't require nudging the cursor for the size hint to update.
        if (_textPreview != null && _drawing.Settings.Tool == ToolKind.Text)
            _textPreview.FontSize = _drawing.Settings.TextSize;

        // Apply the new thickness live to whichever shape the user is currently
        // dragging so the visual matches the cursor preview immediately.
        if (_activeStrokeVisual != null)
            _activeStrokeVisual.StrokeThickness = _drawing.Settings.PencilThickness;
        if (_activeRectVisual != null)
            _activeRectVisual.StrokeThickness = _drawing.Settings.Tool == ToolKind.Ellipse
                ? _drawing.Settings.EllipseThickness
                : _drawing.Settings.RectangleThickness;
        if (_activeLineVisual != null)
            _activeLineVisual.StrokeThickness = _drawing.Settings.LineThickness;
        if (_activeArrowVisual != null)
        {
            _activeArrowVisual.StrokeThickness = _drawing.Settings.LineThickness;
            _activeArrowVisual.Data = BuildArrowGeometry(_activeRectAnchor, _activeArrowEnd, _drawing.Settings.LineThickness);
        }

        e.Handled = true;
    }

    private void UpdatePreviewForThickness(double _thickness)
    {
        var d = _drawing.Settings.PreviewDiameter;
        _pencilPreview.Width = d;
        _pencilPreview.Height = d;
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

    // ---------- Keyboard ----------

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsCtrlDown();
        if (_activeTextBox != null) return; // typing in textbox; handled by it

        // Modifier held over a draw tool → enter temp eyedropper immediately,
        // even without a mouse move.
        if (!_inOcrMode && _drawing.Settings.Tool != ToolKind.None
            && (e.Key == VirtualKey.Menu || e.Key == VirtualKey.Control)
            && IsEyedropperModifierDown())
        {
            EnterTempEyedropper();
            e.Handled = true;
            return;
        }

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
                // Don't intercept when eyedropper is active or a text field has focus.
                if (_eyedropperActive) return;
                if (FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox) return;
                e.Handled = true;
                if (_inOcrMode) { _ = CopyOcrTextAsync(); return; }
                _ = CopyAsync();
                return;
            case VirtualKey.Number1 or VirtualKey.Number2 or VirtualKey.Number3
                 or VirtualKey.Number4 when !ctrl:
                if (HandleToolHotkey(e.Key)) e.Handled = true;
                return;
        }
    }

    // Number keys mirror the toolbar tools; pressing the active tool's key
    // again deselects it, matching the click toggles.
    private bool HandleToolHotkey(VirtualKey key)
    {
        if (!_hasSelection || _inOcrMode || _eyedropperActive) return false;
        if (FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox) return false;
        switch (key)
        {
            case VirtualKey.Number1:
                SetTool(_drawing.Settings.Tool == ToolKind.Pencil ? ToolKind.None : ToolKind.Pencil);
                return true;
            case VirtualKey.Number2:
                SetTool(_drawing.Settings.Tool == ToolKind.Text ? ToolKind.None : ToolKind.Text);
                return true;
            case VirtualKey.Number3:
                SetTool(_drawing.Settings.Tool == _currentShapeTool ? ToolKind.None : _currentShapeTool);
                return true;
            case VirtualKey.Number4:
                _ = EnterOcrModeAsync();
                return true;
        }
        return false;
    }

    private void HandleEscape()
    {
        if (_eyedropperActive)
        {
            ExitEyedropperMode();
            return;
        }
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
        CloseDeferred();
    }

    private static bool IsCtrlDown()
    {
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    // ---------- Hold-to-eyedrop ----------

    private bool _tempEyedropper;
    private Point _lastPointerPos;

    private static bool IsEyedropperModifierDown()
    {
        var key = SettingsService.Instance.Settings.EyedropperModifier == "Ctrl"
            ? VirtualKey.Control : VirtualKey.Menu;
        var state = InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_tempEyedropper
            && (e.Key == VirtualKey.Menu || e.Key == VirtualKey.Control)
            && !IsEyedropperModifierDown())
        {
            ExitTempEyedropper(pick: true);
            e.Handled = true;
        }
    }

    private void EnterTempEyedropper()
    {
        if (_tempEyedropper || _eyedropperActive) return;
        if (_drawing.Settings.Tool == ToolKind.None || _inOcrMode) return;
        EnsureEyedropperBitmap();
        if (_eyedropperPixels == null) return;
        _tempEyedropper = true;
        _magBitmap ??= new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(128, 128);
        MagBrush.ImageSource = _magBitmap;
        _eyedropperActive = true;
        EyedropperMagnifier.Visibility = Visibility.Visible;
        UpdateMagnifier(_lastPointerPos);
    }

    private void ExitTempEyedropper(bool pick)
    {
        if (!_tempEyedropper) return;
        _tempEyedropper = false;
        if (pick) ApplyPickedColor(SamplePixel(_lastPointerPos));
        _eyedropperActive = false;
        EyedropperMagnifier.Visibility = Visibility.Collapsed;
    }
}
