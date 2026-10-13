using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Clipsy.Drawing;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Point = Windows.Foundation.Point;

namespace Clipsy.Views;

public sealed partial class CaptureOverlayWindow
{
    // ---------- Shapes flyout ----------

    private bool _shapesClickHandled = false;

    private void OnShapesClick(object sender, RoutedEventArgs e)
    {
        _shapesClickHandled = true;

        // Cancel hover timer to prevent flyout opening after click
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }

        if (ShapesFlyout != null)
        {
            ShapesFlyout.Visibility = Visibility.Collapsed;
        }

        SetTool(_drawing.Settings.Tool == _currentShapeTool ? ToolKind.None : _currentShapeTool);

        var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        resetTimer.Tick += (s, args) => { _shapesClickHandled = false; resetTimer.Stop(); };
        resetTimer.Start();
    }

    private static void FadeOutFlyout(FrameworkElement flyout)
    {
        if (flyout.Visibility == Visibility.Collapsed) return;
        var anim = new DoubleAnimation { From = 1.0, To = 0.0, Duration = new Duration(TimeSpan.FromMilliseconds(100)), EnableDependentAnimation = true };
        var sb = new Storyboard();
        Storyboard.SetTarget(anim, flyout);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Completed += (_, _) => { flyout.Visibility = Visibility.Collapsed; flyout.Opacity = 1.0; };
        sb.Begin();
    }

    private static void ShowFlyout(FrameworkElement flyout)
    {
        flyout.Opacity = 0.0;
        flyout.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation { From = 0.0, To = 1.0, Duration = new Duration(TimeSpan.FromMilliseconds(120)), EnableDependentAnimation = true };
        var sb = new Storyboard();
        Storyboard.SetTarget(anim, flyout);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void OnShapesPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (ShapesFlyout == null || ShapesBtn == null || _shapesClickHandled) return;
        if (FontsFlyout != null) FadeOutFlyout(FontsFlyout);

        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hoverTimer.Tick += OnHoverTimerTick;
        _hoverTimer.Start();
    }

    private void OnHoverTimerTick(object? sender, object e)
    {
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }

        if (ShapesFlyout == null || ShapesBtn == null) return;

        PositionShapesFlyout();
        ShowFlyout(ShapesFlyout);
    }

    private void PositionShapesFlyout()
    {
        if (ShapesFlyout == null || ShapesBtn == null) return;

        // A Collapsed element measures 0x0 and broke alignment; reveal at opacity
        // 0 first, then ShowFlyout fades it in.
        ShapesFlyout.Opacity = 0.0;
        ShapesFlyout.Visibility = Visibility.Visible;
        ShapesFlyout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var flyoutSize = ShapesFlyout.DesiredSize;

        var transform = ShapesBtn.TransformToVisual(RootGrid);
        var buttonPos = transform.TransformPoint(new Point(0, 0));

        double x = buttonPos.X + ShapesBtn.ActualWidth + 8;
        double y = buttonPos.Y;

        // Flip to the left side if it would overflow the right edge.
        if (x + flyoutSize.Width > RootGrid.ActualWidth - 8)
        {
            x = buttonPos.X - flyoutSize.Width - 8;
        }
        y = System.Math.Clamp(y, 8, System.Math.Max(8, RootGrid.ActualHeight - flyoutSize.Height - 8));

        Canvas.SetLeft(ShapesFlyout, x);
        Canvas.SetTop(ShapesFlyout, y);
    }

    private void OnShapesPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }

        // Brief delay so the cursor can move onto the flyout without closing it.
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _hoverTimer.Tick += (s, args) => {
            if (ShapesFlyout != null) FadeOutFlyout(ShapesFlyout);
            _hoverTimer?.Stop();
            _hoverTimer = null;
        };
        _hoverTimer.Start();
    }

    private void OnShapesFlyoutPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }
    }

    private void OnShapesFlyoutPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (ShapesFlyout != null) FadeOutFlyout(ShapesFlyout);
    }

    // ---------- Text / Fonts flyout (mirrors Shapes flyout) ----------

    private bool _textClickHandled;

    private void OnTextClick(object sender, RoutedEventArgs e)
    {
        _textClickHandled = true;

        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }

        if (FontsFlyout != null) FontsFlyout.Visibility = Visibility.Collapsed;
        // Toggle: re-click active text tool deselects
        SetTool(_drawing.Settings.Tool == ToolKind.Text ? ToolKind.None : ToolKind.Text);

        var resetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        resetTimer.Tick += (s, args) => { _textClickHandled = false; resetTimer.Stop(); };
        resetTimer.Start();
    }

    private void OnTextPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (FontsFlyout == null || TextBtn == null || _textClickHandled) return;

        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }

        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hoverTimer.Tick += OnFontHoverTimerTick;
        _hoverTimer.Start();
        _ = InstalledFonts.Value; // start enumerating during the hover delay
    }

    private void OnFontHoverTimerTick(object? sender, object e)
    {
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnFontHoverTimerTick;
            _hoverTimer = null;
        }
        if (FontsFlyout == null || TextBtn == null) return;
        if (ShapesFlyout != null) FadeOutFlyout(ShapesFlyout);
        EnsureFontListBuilt();
        PositionFontsFlyout();
        ShowFlyout(FontsFlyout);
    }

    private const string BundledFontEntry = "Onest (bundled)";
    // Each row renders in its own font; materializing hundreds of them stalls the UI thread.
    private const int MaxFontRows = 200;

    // Enumerated once per process, off the UI thread: GDI+ is slow with many fonts installed.
    private static readonly Lazy<Task<IReadOnlyList<string>>> InstalledFonts =
        new(() => Task.Run(() => EnumerateInstalledFonts()));

    private IReadOnlyList<string>? _systemFonts;
    private bool _fontsLoading;
    private DispatcherQueueTimer? _fontFilterTimer;

    private static IReadOnlyList<string> EnumerateInstalledFonts()
    {
        List<string> fonts;
        try
        {
            // GDI+ font enumeration via System.Drawing.Common; filter to families
            // with a regular face to skip broken icon/symbol entries.
            using var coll = new System.Drawing.Text.InstalledFontCollection();
            fonts = coll.Families
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Font enumeration failed: {ex.Message}");
            fonts = new List<string> { "Segoe UI Variable", "Segoe UI", "Arial" };
        }
        // Prepend the bundled Onest entry so it always shows even if not
        // installed system-wide.
        fonts.Insert(0, BundledFontEntry);
        return fonts;
    }

    private async void EnsureFontListBuilt()
    {
        if (FontList == null || _systemFonts != null || _fontsLoading) return;
        _fontsLoading = true;
        try { _systemFonts = await InstalledFonts.Value; }
        finally { _fontsLoading = false; }
        if (_closed) return;
        RenderFontList(FontFilterBox?.Text ?? string.Empty);
        // The flyout may already be open, placed for the empty list; keep it on screen.
        if (FontsFlyout is { Visibility: Visibility.Visible })
        {
            FontsFlyout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double maxTop = System.Math.Max(8, RootGrid.ActualHeight - FontsFlyout.DesiredSize.Height - 8);
            Canvas.SetTop(FontsFlyout, System.Math.Clamp(Canvas.GetTop(FontsFlyout), 8, maxTop));
        }
    }

    private void RenderFontList(string filter)
    {
        if (FontList == null || _systemFonts == null) return;
        FontList.Children.Clear();
        IEnumerable<string> items = _systemFonts;
        if (!string.IsNullOrWhiteSpace(filter))
            items = items.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase));
        foreach (var name in items.Take(MaxFontRows))
        {
            var (family, tag) = name == BundledFontEntry
                ? ("ms-appx:///Assets/Fonts/Onest-VariableFont_wght.ttf#Onest, Inter, Segoe UI, sans-serif",
                   "ms-appx:///Assets/Fonts/Onest-VariableFont_wght.ttf#Onest, Inter, Segoe UI, sans-serif")
                : (name, name);
            var preview = new TextBlock
            {
                Text = name,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
            };
            try { preview.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(family); }
            catch { /* fall back to inherited */ }
            var btn = new Button
            {
                Content = preview,
                Tag = tag,
                Style = (Microsoft.UI.Xaml.Style)Application.Current.Resources["ClipsyButtonGhost"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 1, 0, 1),
            };
            btn.Click += OnFontPick;
            FontList.Children.Add(btn);
        }
    }

    private void OnFontFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (FontFilterBox == null) return;
        // Debounced: rebuilding the rows on every keystroke makes typing lag.
        if (_fontFilterTimer == null)
        {
            _fontFilterTimer = DispatcherQueue.CreateTimer();
            _fontFilterTimer.Interval = TimeSpan.FromMilliseconds(150);
            _fontFilterTimer.IsRepeating = false;
            _fontFilterTimer.Tick += (_, _) =>
            {
                if (!_closed) RenderFontList(FontFilterBox?.Text ?? string.Empty);
            };
        }
        _fontFilterTimer.Stop();
        _fontFilterTimer.Start();
    }

    private void PositionFontsFlyout()
    {
        if (FontsFlyout == null || TextBtn == null) return;
        // Same Collapsed-measures-to-zero pitfall as PositionShapesFlyout.
        FontsFlyout.Opacity = 0.0;
        FontsFlyout.Visibility = Visibility.Visible;
        FontsFlyout.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var flyoutSize = FontsFlyout.DesiredSize;
        var transform = TextBtn.TransformToVisual(RootGrid);
        var buttonPos = transform.TransformPoint(new Point(0, 0));
        double x = buttonPos.X + TextBtn.ActualWidth + 8;
        double y = buttonPos.Y; // top-aligned with the button
        if (x + flyoutSize.Width > RootGrid.ActualWidth - 8)
            x = buttonPos.X - flyoutSize.Width - 8;
        y = System.Math.Clamp(y, 8, System.Math.Max(8, RootGrid.ActualHeight - flyoutSize.Height - 8));
        Canvas.SetLeft(FontsFlyout, x);
        Canvas.SetTop(FontsFlyout, y);
    }

    private void OnTextPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnFontHoverTimerTick;
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer = null;
        }
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _hoverTimer.Tick += (s, args) =>
        {
            if (FontsFlyout != null) FadeOutFlyout(FontsFlyout);
            _hoverTimer?.Stop();
            _hoverTimer = null;
        };
        _hoverTimer.Start();
    }

    private void OnFontsFlyoutPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_hoverTimer != null)
        {
            _hoverTimer.Stop();
            _hoverTimer.Tick -= OnHoverTimerTick;
            _hoverTimer.Tick -= OnFontHoverTimerTick;
            _hoverTimer = null;
        }
    }

    private void OnFontsFlyoutPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (FontsFlyout != null) FadeOutFlyout(FontsFlyout);
    }

    private void OnFontPick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string family) return;
        _drawing.Settings.TextFont = family;
        SetTool(ToolKind.Text);
        // Reflect choice on the toolbar T glyph so the user sees current font.
        if (TextBtnGlyph != null)
        {
            try
            {
                var ff = new Microsoft.UI.Xaml.Media.FontFamily(family);
                TextBtnGlyph.FontFamily = ff;
                if (TextBtnGlyphSmall != null) TextBtnGlyphSmall.FontFamily = ff;
            }
            catch { /* fallback to inherited font */ }
        }
    }

    private void OnShapePick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        var tool = tag switch
        {
            "Pencil" => ToolKind.Pencil,
            "Rectangle" => ToolKind.Rectangle,
            "Ellipse" => ToolKind.Ellipse,
            "Line" => ToolKind.Line,
            "Arrow" => ToolKind.Arrow,
            "Text" => ToolKind.Text,
            _ => ToolKind.None,
        };
        // Cache shape selection before SetTool so the ShapesBtn icon renders the
        // new pick (it reads _currentShapeTool), not the previous one.
        if (tool is ToolKind.Rectangle or ToolKind.Ellipse or ToolKind.Line or ToolKind.Arrow)
        {
            _currentShapeTool = tool;
        }

        // Toggle: re-click active tool deselects
        SetTool(_drawing.Settings.Tool == tool ? ToolKind.None : tool);
    }
}
