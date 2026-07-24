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

    // Drag handle above the active TextBox to move in-progress text before
    // commit; tracked so CancelText / CommitText can remove it.
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
        // Subtree-scoped implicit styles override the app-global ones that pin a
        // default font on the TextBox's inner text host; set before it enters the
        // tree so the template resolves them.
        InstallScopedFont(tb, family);
        // Offset so the first glyph's center sits on the click point; re-adjusts
        // on the first keystroke once the typed character is known.
        double tbLeft = pos.X - TextEntryPadding.Left - glyphW / 2;
        double tbTop  = pos.Y - TextEntryPadding.Top  - glyphH / 2;
        Canvas.SetLeft(tb, tbLeft);
        Canvas.SetTop(tb,  tbTop);
        DrawingCanvas.Children.Add(tb);
        _activeTextBox = tb;
        _activeTextAnchor = pos;
        _activeTextAnchorApplied = false;
        _liveFontApplied = false;
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
        // The built-in clear (X) button scales with font size and sits off-screen
        // when text overflows; hide it once the template is realized.
        tb.Loaded += (_, _) => HideTextBoxClearButton(tb);
        // Eat pointer events so RootGrid handlers don't re-trigger StartToolPress
        // when the user clicks inside the active text box.
        tb.PointerPressed += (_, ev) => ev.Handled = true;
        tb.PointerReleased += (_, ev) => ev.Handled = true;
        DrawingCanvas.IsHitTestVisible = true;

        // Drag handle: small pill above the textbox; click-drag moves it. Neutral
        // grey + Move glyph so it doesn't read as a close/danger button.
        var handle = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x2E, 0x2E, 0x32)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x60, 0x60, 0x66)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            Child = new FontIcon
            {
                Glyph = "\uE7C2", // Move glyph (escaped so it survives editors)
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            },
        };
        Canvas.SetLeft(handle, tbLeft);
        Canvas.SetTop(handle, tbTop - 30); // sit just above the box
        DrawingCanvas.Children.Add(handle);
        _activeDragHandle = handle;

        handle.PointerPressed += OnDragHandlePressed;
        handle.PointerMoved   += OnDragHandleMoved;
        handle.PointerReleased += OnDragHandleReleased;
        handle.PointerCaptureLost += (_, _) => _draggingActiveText = false;

        tb.Focus(FocusState.Programmatic);
        // The internal TextBoxView that renders typed text is realized on focus,
        // after Loaded — re-apply the font once it exists so it isn't left on the
        // implicit default. Reinforced on the first keystroke.
        DispatcherQueue.TryEnqueue(() => ReapplyLiveFont(tb));
    }

    private bool _liveFontApplied;

    // Install subtree-scoped implicit styles so the picked font wins over the
    // app-global implicit styles that force a default font on the TextBox's
    // inner text host (WinUI 3 does not inherit FontFamily into it).
    private static void InstallScopedFont(TextBox tb, Microsoft.UI.Xaml.Media.FontFamily fam)
    {
        try
        {
            var rd = new ResourceDictionary();
            void Add(System.Type t, Microsoft.UI.Xaml.DependencyProperty prop)
            {
                var st = new Style(t);
                st.Setters.Add(new Setter(prop, fam));
                rd.Add(t, st);
            }
            Add(typeof(TextBlock), TextBlock.FontFamilyProperty);
            Add(typeof(ContentPresenter), ContentPresenter.FontFamilyProperty);
            tb.Resources = rd;
        }
        catch { /* keep inherited */ }
    }

    private void ReapplyLiveFont(TextBox tb)
    {
        try
        {
            var fam = new Microsoft.UI.Xaml.Media.FontFamily(_drawing.Settings.TextFont);
            tb.FontFamily = fam;
            ApplyFontToDescendants(tb, fam);
            _liveFontApplied = true;
        }
        catch { /* keep inherited */ }
    }

    private static void ApplyFontToDescendants(Microsoft.UI.Xaml.DependencyObject root, Microsoft.UI.Xaml.Media.FontFamily fam)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock tbk) tbk.FontFamily = fam;
            else if (child is ContentPresenter cp) cp.FontFamily = fam;
            else if (child is Control ctl) ctl.FontFamily = fam;
            ApplyFontToDescendants(child, fam);
        }
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
        Canvas.SetTop(_activeDragHandle, newTop - 30);
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
        if (_activeTextBox == null) return;
        // First keystroke realizes the text view; force the picked font onto it.
        if (!_liveFontApplied) ReapplyLiveFont(_activeTextBox);
        if (_activeTextAnchorApplied) return;
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
        // Move the handle with the box; it was anchored to the pre-recenter
        // position and visibly jumped apart on the first keystroke.
        if (_activeDragHandle != null)
        {
            Canvas.SetLeft(_activeDragHandle, newLeft);
            Canvas.SetTop(_activeDragHandle, newTop - 30);
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

    private static void HideTextBoxClearButton(Microsoft.UI.Xaml.DependencyObject root)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Button b && b.Name == "DeleteButton")
            {
                // Remove from the tree: a plain Collapse is overridden by the
                // TextBox's own visual states when text is entered.
                if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(b) is Panel p)
                    p.Children.Remove(b);
                else
                {
                    b.Visibility = Visibility.Collapsed;
                    b.Width = 0;
                }
                return;
            }
            HideTextBoxClearButton(child);
        }
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
        // Measure with an off-tree TextBlock; the committed glyph itself renders
        // on the GPU canvas, not as a XAML node.
        var probe = new TextBlock
        {
            Text = text,
            FontFamily = family,
            FontSize = _drawing.Settings.TextSize,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _drawing.Add(new TextElement
        {
            Position = new Point(x, y),
            Text = text,
            FontSize = _drawing.Settings.TextSize,
            FontFamily = _drawing.Settings.TextFont,
            MeasuredSize = probe.DesiredSize,
            Color = _drawing.Settings.Color,
        });
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
