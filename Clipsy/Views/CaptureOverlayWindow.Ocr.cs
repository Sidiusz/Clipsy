using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Clipsy.Drawing;
using Clipsy.Localization;
using Clipsy.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;

namespace Clipsy.Views;

public sealed partial class CaptureOverlayWindow
{
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
        OcrPanelsContainer.Visibility = Visibility.Collapsed;
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
        OcrPanelsContainer.Visibility = Visibility.Collapsed;
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
        ShapesBtn.IsEnabled = enabled;
        ColorBtn.IsEnabled = enabled;
        OcrBtn.IsEnabled = enabled;
        if (MoveBtn != null) MoveBtn.IsEnabled = enabled;
        PencilBtn.IsEnabled = enabled;
        EllipseBtn.IsEnabled = enabled;
        RectBtn.IsEnabled = enabled;
        LineBtn.IsEnabled = enabled;
        TextBtn.IsEnabled = enabled;
        if (!enabled && ShapesFlyout != null)
        {
            ShapesFlyout.Visibility = Visibility.Collapsed;
        }
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
        ScanLine.Visibility = Visibility.Visible;
        ScanLine.Width = _selectionRect.Width;
        Canvas.SetLeft(ScanLine, 0);
        Canvas.SetTop(ScanLine, 0);
        _scanProgress = 0;
        _scanDir = 1.0;
        _scanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scanTimer.Tick += OnScanTick;
        _scanTimer.Start();
    }

    private void OnScanTick(object? sender, object e)
    {
        _scanProgress += 0.015 * _scanDir;
        if (_scanProgress >= 1.0) { _scanProgress = 1.0; _scanDir = -1.0; }
        else if (_scanProgress <= 0.0) { _scanProgress = 0.0; _scanDir = 1.0; }
        var t = _scanProgress;
        var eased = t * t * (3.0 - 2.0 * t);
        double maxY = System.Math.Max(0, _selectionRect.Height - ScanLine.Height);
        Canvas.SetTop(ScanLine, eased * maxY);
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
        OcrToolbar.Visibility = Visibility.Collapsed;
        OcrPanelsContainer.Visibility = Visibility.Visible;
        PositionOcrPanelsContainer();
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

    private void PositionOcrPanelsContainer()
    {
        OcrPanelsContainer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var sz = OcrPanelsContainer.DesiredSize;
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
        Canvas.SetLeft(OcrPanelsContainer, tx);
        Canvas.SetTop(OcrPanelsContainer, ty);
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

    private string? _lastTranslateSource;

    private async void OnOcrTranslate(object sender, RoutedEventArgs e)
    {
        string text = OcrTextBox.SelectionLength > 0
            ? OcrTextBox.SelectedText
            : OcrTextBox.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        await DoTranslateAsync(text);
    }

    private async System.Threading.Tasks.Task DoTranslateAsync(string text)
    {
        _lastTranslateSource = text;
        TranslateTarget.Text = "...";
        TranslatePanel.Width = OcrTextPanel.ActualWidth;
        TranslatePanel.Visibility = Visibility.Visible;
        UpdateTranslateButtons();

        var cfg = SettingsService.Instance.Settings;
        string from = cfg.TranslateFrom;
        string to   = cfg.TranslateTo == "ui" ? Strings.Lang : cfg.TranslateTo;

        // MyMemory doesn't support sl=auto; fall back to heuristic detection
        if (from == "auto" && !string.Equals(cfg.TranslateService, "Google", StringComparison.OrdinalIgnoreCase))
        {
            var guessed = TranslationService.GuessLangPair(text);
            from = guessed.from;
            if (cfg.TranslateTo == "ui") to = guessed.to;
        }

        var translated = await TranslationService.TranslateAsync(text, from, to, cfg.TranslateService);
        TranslateTarget.Text = translated ?? Strings.Get("TranslateUnavailable");
    }

    private void UpdateTranslateButtons()
    {
        if (TranslateFromBtn == null || TranslateToBtn == null) return;
        var s = SettingsService.Instance.Settings;
        TranslateFromBtn.Content = LangBadge(s.TranslateFrom);
        TranslateToBtn.Content   = LangBadge(s.TranslateTo == "ui" ? Strings.Lang : s.TranslateTo);
    }

    private static string LangBadge(string code) => code.ToLowerInvariant() switch
    {
        "auto" => "AUTO",
        "ui"   => Strings.Lang.ToUpperInvariant(),
        _      => code.ToUpperInvariant()
    };

    private void OnTranslateFromBtnClick(object sender, RoutedEventArgs e)
        => ShowLangFlyout((Button)sender, isFrom: true);

    private void OnTranslateToBtnClick(object sender, RoutedEventArgs e)
        => ShowLangFlyout((Button)sender, isFrom: false);

    private void ShowLangFlyout(Button anchor, bool isFrom)
    {
        var flyout = new MenuFlyout();
        var cfg = SettingsService.Instance.Settings;
        bool google = string.Equals(cfg.TranslateService, "Google", StringComparison.OrdinalIgnoreCase);

        if (isFrom && google)
        {
            var auto = new MenuFlyoutItem { Text = Strings.Get("LangAutoDetect") };
            auto.Click += async (_, _) => await SetTranslateLangAsync("auto", isFrom);
            flyout.Items.Add(auto);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }
        if (!isFrom)
        {
            var ui = new MenuFlyoutItem { Text = Strings.Get("LangUiDefault") };
            ui.Click += async (_, _) => await SetTranslateLangAsync("ui", isFrom);
            flyout.Items.Add(ui);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }
        foreach (var lang in TranslationService.LangCatalog)
        {
            var code = lang.Code;
            string label = (Strings.Lang == "ru" ? lang.Ru : lang.En) + $"  ({code.ToUpperInvariant()})";
            var item = new MenuFlyoutItem { Text = label };
            item.Click += async (_, _) => await SetTranslateLangAsync(code, isFrom);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(anchor);
    }

    private async System.Threading.Tasks.Task SetTranslateLangAsync(string code, bool isFrom)
    {
        var s = SettingsService.Instance.Settings;
        if (isFrom) s.TranslateFrom = code;
        else        s.TranslateTo   = code;
        SettingsService.Instance.Save();
        UpdateTranslateButtons();
        if (!string.IsNullOrEmpty(_lastTranslateSource))
            await DoTranslateAsync(_lastTranslateSource);
    }

    private void OnOcrPanelDragStart(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(OverlayLayer).Position;
        _ocrPanelDragOffset = new Point(
            pos.X - Canvas.GetLeft(OcrPanelsContainer),
            pos.Y - Canvas.GetTop(OcrPanelsContainer));
        _ocrPanelDragging = ((UIElement)sender).CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnOcrPanelDragMove(object sender, PointerRoutedEventArgs e)
    {
        if (!_ocrPanelDragging) return;
        var pos = e.GetCurrentPoint(OverlayLayer).Position;
        double x = pos.X - _ocrPanelDragOffset.X;
        double y = pos.Y - _ocrPanelDragOffset.Y;
        x = System.Math.Clamp(x, 0, System.Math.Max(0, RootGrid.ActualWidth - OcrPanelsContainer.ActualWidth));
        y = System.Math.Clamp(y, 0, System.Math.Max(0, RootGrid.ActualHeight - OcrPanelsContainer.ActualHeight));
        Canvas.SetLeft(OcrPanelsContainer, x);
        Canvas.SetTop(OcrPanelsContainer, y);
        e.Handled = true;
    }

    private void OnOcrPanelDragEnd(object sender, PointerRoutedEventArgs e)
    {
        if (_ocrPanelDragging)
        {
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
            _ocrPanelDragging = false;
        }
        e.Handled = true;
    }

    private void OnOcrPanelDragCancel(object sender, PointerRoutedEventArgs e)
    {
        _ocrPanelDragging = false;
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
