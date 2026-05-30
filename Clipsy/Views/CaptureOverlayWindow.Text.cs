using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Clipsy.Drawing;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Point = Windows.Foundation.Point;

namespace Clipsy.Views;

public sealed partial class CaptureOverlayWindow
{
    // ---------- Text entry ----------

    // Padding inside the active TextBox. Extracted so StartTextEntry and
    // CommitText agree on the visual offset between the box and the glyph.
    private static readonly Thickness TextEntryPadding = new(4, 2, 4, 2);

    // Drag handle that sits above the active TextBox so the user can move
    // the in-progress text around the screen before committing it. Tracked
    // so CancelText / CommitText can find and remove it.
    private Border? _activeDragHandle;
    private bool _draggingActiveText;
    private Point _dragStartPointer;
    private double _dragStartTbLeft;
    private double _dragStartTbTop;

    private void StartTextEntry(Point pos)
    {
        // Commit any prior entry before opening a new one.
        if (_activeTextBox != null) CommitText();

        var family = new Microsoft.UI.Xaml.Media.FontFamily(_drawing.Settings.TextFont);
        var (glyphW, glyphH) = MeasureGlyph("M", _drawing.Settings.TextSize, family);

        var tb = new TextBox
        {
            MinWidth = 80,
            AcceptsReturn = false,
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            Foreground = new SolidColorBrush(_drawing.Settings.Color),
            BorderBrush = new SolidColorBrush(_drawing.Settings.Color),
            BorderThickness = new Thickness(1),
            FontFamily = family,
            FontSize = _drawing.Settings.TextSize,
            Padding = TextEntryPadding,
        };
        // Offset so the first glyph's optical center sits on the click point
        // instead of the TextBox top-left corner. Adjusts again on the first
        // keystroke once the actual typed character is known.
        double tbLeft = pos.X - TextEntryPadding.Left - glyphW / 2;
        double tbTop  = pos.Y - TextEntryPadding.Top  - glyphH / 2;
        Canvas.SetLeft(tb, tbLeft);
        Canvas.SetTop(tb,  tbTop);
        DrawingCanvas.Children.Add(tb);
        _activeTextBox = tb;
        _activeTextAnchor = pos;
        _activeTextAnchorApplied = false;
        tb.LostFocus += (_, _) =>
        {
            // Don't commit while the user is dragging the handle — focus moves
            // off the textbox during drag.
            if (_draggingActiveText) return;
            CommitText();
        };
        tb.KeyDown += (_, ke) =>
        {
            if (ke.Key == VirtualKey.Enter) { ke.Handled = true; CommitText(); }
            else if (ke.Key == VirtualKey.Escape) { ke.Handled = true; CancelText(); }
        };
        tb.TextChanged += OnActiveTextBoxTextChanged;
        // Eat pointer events so RootGrid handlers don't re-trigger StartToolPress
        // when the user clicks inside the active text box.
        tb.PointerPressed += (_, ev) => ev.Handled = true;
        tb.PointerReleased += (_, ev) => ev.Handled = true;
        DrawingCanvas.IsHitTestVisible = true;

        // Drag handle: small pill above the textbox. Click-drag moves the
        // textbox to a new screen position. Neutral dark grey + Fluent Move
        // glyph so it doesn't read as a close/danger button.
        var handle = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x2E, 0x2E, 0x32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0x60, 0x66)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Child = new FontIcon
            {
                Glyph = "", // Move (Segoe Fluent Icons)
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };
        ToolTipService.SetToolTip(handle, "Drag to reposition");
        Canvas.SetLeft(handle, tbLeft);
        Canvas.SetTop(handle, tbTop - 18); // sit just above the box
        DrawingCanvas.Children.Add(handle);
        _activeDragHandle = handle;

        handle.PointerPressed += OnDragHandlePressed;
        handle.PointerMoved   += OnDragHandleMoved;
        handle.PointerReleased += OnDragHandleReleased;
        handle.PointerCaptureLost += (_, _) => _draggingActiveText = false;

        tb.Focus(FocusState.Programmatic);
    }

    private void OnDragHandlePressed(object sender, PointerRoutedEventArgs e)
    {
        if (_activeTextBox == null || sender is not UIElement el) return;
        _draggingActiveText = true;
        _dragStartPointer = e.GetCurrentPoint(DrawingCanvas).Position;
        _dragStartTbLeft = Canvas.GetLeft(_activeTextBox);
        _dragStartTbTop  = Canvas.GetTop(_activeTextBox);
        el.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnDragHandleMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingActiveText || _activeTextBox == null || _activeDragHandle == null) return;
        var p = e.GetCurrentPoint(DrawingCanvas).Position;
        double dx = p.X - _dragStartPointer.X;
        double dy = p.Y - _dragStartPointer.Y;
        double newLeft = _dragStartTbLeft + dx;
        double newTop  = _dragStartTbTop  + dy;
        Canvas.SetLeft(_activeTextBox, newLeft);
        Canvas.SetTop(_activeTextBox,  newTop);
        Canvas.SetLeft(_activeDragHandle, newLeft);
        Canvas.SetTop(_activeDragHandle, newTop - 18);
        // Move the anchor too so future re-centering on first keystroke
        // (if it hasn't fired yet) stays consistent with the new position.
        _activeTextAnchor = new Point(
            _activeTextAnchor.X + dx,
            _activeTextAnchor.Y + dy);
        _dragStartPointer = p;
        _dragStartTbLeft = newLeft;
        _dragStartTbTop  = newTop;
        e.Handled = true;
    }

    private void OnDragHandleReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is UIElement el) el.ReleasePointerCaptures();
        _draggingActiveText = false;
        // Return focus to the textbox so the user can keep typing without
        // an extra click.
        _activeTextBox?.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private Point _activeTextAnchor;
    private bool  _activeTextAnchorApplied;

    private void OnActiveTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_activeTextBox == null || _activeTextAnchorApplied) return;
        var text = _activeTextBox.Text;
        if (string.IsNullOrEmpty(text)) return;
        // Re-measure with the actual first character so off-width glyphs
        // (e.g. "i" vs "M") still end up centered on the click point.
        var family = _activeTextBox.FontFamily;
        var (gw, gh) = MeasureGlyph(text[0].ToString(), _activeTextBox.FontSize, family);
        double newLeft = _activeTextAnchor.X - TextEntryPadding.Left - gw / 2;
        double newTop  = _activeTextAnchor.Y - TextEntryPadding.Top  - gh / 2;
        Canvas.SetLeft(_activeTextBox, newLeft);
        Canvas.SetTop(_activeTextBox,  newTop);
        // Drag handle was anchored to the pre-recenter position, so it would
        // visibly jump apart from the box on the first keystroke. Move it
        // with the box.
        if (_activeDragHandle != null)
        {
            Canvas.SetLeft(_activeDragHandle, newLeft);
            Canvas.SetTop(_activeDragHandle, newTop - 18);
        }
        _activeTextAnchorApplied = true;
    }

    private static (double w, double h) MeasureGlyph(string ch, double size, Microsoft.UI.Xaml.Media.FontFamily family)
    {
        var probe = new TextBlock
        {
            Text = ch,
            FontSize = size,
            FontFamily = family,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return (probe.DesiredSize.Width, probe.DesiredSize.Height);
    }

    private void CommitText()
    {
        if (_activeTextBox == null) return;
        var text = _activeTextBox.Text ?? string.Empty;
        // Preserve the TextBox's internal padding so the committed TextBlock
        // glyph sits at the same on-screen position as it did during entry.
        double x = Canvas.GetLeft(_activeTextBox) + TextEntryPadding.Left;
        double y = Canvas.GetTop(_activeTextBox)  + TextEntryPadding.Top;
        var owning = _activeTextBox;
        var family = owning.FontFamily;
        owning.TextChanged -= OnActiveTextBoxTextChanged;
        _activeTextBox = null;
        DrawingCanvas.Children.Remove(owning);
        if (_activeDragHandle != null)
        {
            DrawingCanvas.Children.Remove(_activeDragHandle);
            _activeDragHandle = null;
        }
        DrawingCanvas.IsHitTestVisible = false;
        if (string.IsNullOrWhiteSpace(text)) return;
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = family,
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
        if (_activeDragHandle != null)
        {
            DrawingCanvas.Children.Remove(_activeDragHandle);
            _activeDragHandle = null;
        }
        DrawingCanvas.IsHitTestVisible = false;
    }
}
