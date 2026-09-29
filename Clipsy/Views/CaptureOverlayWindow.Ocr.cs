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

    private int _ocrRun;
    private System.Threading.CancellationTokenSource? _ocrCts;
    private ScreenshotRenderer.PixelRect _ocrCrop;

    private async Task EnterOcrModeAsync()
    {
        if (!_hasSelection || _inOcrMode) return;
        _inOcrMode = true;
        int run = ++_ocrRun;
        _ocrCts?.Cancel();
        var cts = _ocrCts = new System.Threading.CancellationTokenSource();
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
        var frame = _frame;
        var selection = _selectionRect;
        double scale = DpiScale;
        _ocrCrop = ScreenshotRenderer.ToPixelRect(selection, scale, frame.PixelWidth, frame.PixelHeight);
        try
        {
            var png = await Task.Run(() => ScreenshotRenderer.RenderPng(frame, selection, Array.Empty<DrawElement>(), scale));
            var engine = OcrEngineFactory.Resolve();
            words = await engine.RecognizeAsync(png, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (OcrUnavailableException ex)
        {
            Diagnostics.Log($"OCR unavailable: {ex.Message}");
            if (run == _ocrRun) NotificationService.Error("ErrOcrUnavailable");
            words = Array.Empty<OcrWord>();
        }
        catch (Exception ex)
        {
            Diagnostics.Log("OCR failed", ex);
            if (run == _ocrRun) NotificationService.Error("ErrOcrFailed");
            words = Array.Empty<OcrWord>();
        }

        // Esc, a re-entered scan or a new capture superseded this run: drop its result.
        if (run != _ocrRun || !_inOcrMode) return;
        StopScanAnimation();
        RenderOcrResults(words);
    }

    private void ExitOcrMode()
    {
        _inOcrMode = false;
        _ocrRun++;
        _ocrCts?.Cancel();
        _ocrCts = null;
        _translateRun++;
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
        foreach (var (_, box) in _ocrVisuals)
            OcrLayer.Children.Remove(box);
        _ocrVisuals.Clear();
        _ocrWordsRaw.Clear();
        _ocrWordsDip.Clear();
    }

    private void RenderOcrResults(IReadOnlyList<OcrWord> words)
    {
        ClearOcrVisuals();
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

        // Word bounds are pixels of the cropped image; map back through the crop origin to root DIPs.
        var scale = DpiScale;
        var ox = _ocrCrop.X / scale;
        var oy = _ocrCrop.Y / scale;

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

            _ocrVisuals.Add((b, rect));
        }

        OcrTextBox.Text = OcrTextLayout.BuildText(words);
        OcrToolbar.Visibility = Visibility.Collapsed;
        OcrPanelsContainer.Visibility = Visibility.Visible;
        PositionOcrPanelsContainer();
        SetOcrButtonsEnabled(true);
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
            await ClipboardService.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("OCR copy failed", ex);
            NotificationService.Error("ErrCopyFailed");
            return;
        }
        OcrStatusLabel.Text = Strings.Get("Copied");
        Canvas.SetLeft(OcrStatusLabel, System.Math.Max(8, _selectionRect.Width / 2 - 30));
        Canvas.SetTop(OcrStatusLabel, 8);
        OcrStatusLabel.Visibility = Visibility.Visible;
        await FadeOutLaterAsync(OcrStatusLabel, 1200);
    }

    private string? _lastTranslateSource;
    private int _translateRun;

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
        int run = ++_translateRun;
        _lastTranslateSource = text;
        TranslateTarget.Text = "...";
        TranslatePanel.Width = OcrTextPanel.ActualWidth;
        TranslatePanel.Visibility = Visibility.Visible;
        UpdateTranslateButtons();

        var cfg = SettingsService.Instance.Settings;
        string from = cfg.TranslateFrom;
        string to   = cfg.TranslateTo == "ui" ? Strings.Lang : cfg.TranslateTo;

        if (!cfg.TranslationNoticeShown)
        {
            string serviceName = TranslationService.DisplayName(cfg.TranslateService);
            NotificationService.Post(NotificationLevel.Info, "Clipsy",
                string.Format(Strings.Get("TranslateNotice"), serviceName), ToastCategory.Hint);
            cfg.TranslationNoticeShown = true;
            SettingsService.Instance.SaveState();
        }

        var result = await TranslationService.TranslateAsync(text, from, to, cfg.TranslateService);
        // A slower earlier request (language switched meanwhile) must not overwrite the newer one.
        if (run != _translateRun || !_inOcrMode) return;
        TranslateTarget.Text = result.Text ?? Strings.Get(result.ErrorKey ?? "TranslateUnavailable");
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
        SettingsService.Instance.SaveState();
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
