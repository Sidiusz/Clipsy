using System;
using System.Threading;
using System.Threading.Tasks;
using Clipsy.Localization;
using Clipsy.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Clipsy.Views.Settings;

public sealed partial class SettingsWindow
{
    private void OnOcrEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateOcrEngineSections();
        MarkChanged();
    }

    private void UpdateOcrEngineSections()
    {
        var selected = SelectedComboTag(OcrEngineBox);
        if (TessLangSection != null)
            TessLangSection.Visibility = string.Equals(selected, "Tesseract", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
        if (PpOcrModelSection != null)
            PpOcrModelSection.Visibility = string.Equals(selected, "PPOCRv5", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildTessLangRows()
    {
        TessLangList.Children.Clear();
        foreach (var lang in TessdataService.Catalog)
            TessLangList.Children.Add(CreateTessLangRow(lang));
    }

    private UIElement CreateTessLangRow(TessdataLang lang)
    {
        var installed = TessdataService.IsInstalled(lang.Code);

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128, GridUnitType.Pixel) });

        // One row, one action (Install/Delete): an installed language is used
        // automatically, so there's no separate "selected" checkbox.
        var nameBlock = new TextBlock
        {
            Text = lang.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["ClipsyBody"],
            Opacity = installed ? 1.0 : 0.7,
        };
        Grid.SetColumn(nameBlock, 0);

        var sizeBlock = new TextBlock
        {
            Text = lang.ApproxSize,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["ClipsyHelper"],
        };
        Grid.SetColumn(sizeBlock, 1);

        var progress = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Value = 0,
            Width = 84,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(progress, 2);

        var btn = new Button
        {
            Content = installed ? CreateTessDeleteContent() : Strings.Get("BtnInstall"),
            Style = (Style)Application.Current.Resources["ClipsyButtonGhost"],
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(btn, 3);

        grid.Children.Add(nameBlock);
        grid.Children.Add(sizeBlock);
        grid.Children.Add(progress);
        grid.Children.Add(btn);

        btn.Click += (_, _) =>
        {
            if (TessdataService.IsInstalled(lang.Code))
            {
                if (!TessdataService.Delete(lang.Code))
                {
                    ShowNotification("ErrDeleteFailed", "error");
                    return;
                }
                _tessSelectedCodes.Remove(lang.Code);
                MarkChanged();
                var idx = TessLangList.Children.IndexOf(grid);
                if (idx >= 0) TessLangList.Children[idx] = CreateTessLangRow(lang);
            }
            else
            {
                _ = DownloadTessLangAsync(lang, grid, btn, progress);
            }
        };

        return grid;
    }

    private StackPanel CreateTessDeleteContent()
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        panel.Children.Add(new FontIcon
        {
            Glyph = "\uE74D",
            FontSize = 12,
            Foreground = ThemeService.GetBrush("ClipsyDangerBrush", Content as FrameworkElement),
        });
        panel.Children.Add(new TextBlock { Text = Strings.Get("BtnDelete"), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }

    private async Task DownloadTessLangAsync(TessdataLang lang, Grid row, Button btn, ProgressBar progressBar)
    {
        if (_tessDownloadCts.TryGetValue(lang.Code, out var existing))
        {
            existing.Cancel();
            _tessDownloadCts.Remove(lang.Code);
        }

        var cts = new CancellationTokenSource();
        _tessDownloadCts[lang.Code] = cts;

        btn.IsEnabled = false;
        btn.Content = Strings.Get("TessInstalling");
        progressBar.Visibility = Visibility.Visible;
        progressBar.Value = 0;

        try
        {
            var p = new Progress<int>(v =>
            {
                if (!_windowClosed) DispatcherQueue.TryEnqueue(() => progressBar.Value = v);
            });
            await TessdataService.DownloadAsync(lang.Code, p, cts.Token);

            if (!_windowClosed)
            {
                _tessSelectedCodes.Add(lang.Code);
                MarkChanged();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Tessdata download failed: {ex.Message}");
            if (!_windowClosed) NotificationService.Error("ErrTessDownload");
        }
        finally
        {
            if (_tessDownloadCts.TryGetValue(lang.Code, out var current) && ReferenceEquals(current, cts))
                _tessDownloadCts.Remove(lang.Code);
            cts.Dispose();
            if (!_windowClosed)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var idx = TessLangList.Children.IndexOf(row);
                    if (idx >= 0) TessLangList.Children[idx] = CreateTessLangRow(lang);
                });
            }
        }
    }

    private void BuildPpOcrModelRows()
    {
        if (PpOcrModelList == null) return;

        LblPpOcrModels.Text = Strings.Get("LblPpOcrModels");
        HelperPpOcrModels.Text = Strings.Get("HelperPpOcrModels");

        PpOcrModelList.Children.Clear();
        foreach (var model in PpOcrV5Service.RecognizerModels)
            PpOcrModelList.Children.Add(CreatePpOcrModelRow(model));
    }

    private UIElement CreatePpOcrModelRow(PpOcrV5RecognizerSpec model)
    {
        bool installed = PpOcrV5Service.IsModelInstalled(model.Key);

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128, GridUnitType.Pixel) });

        var nameBlock = new TextBlock
        {
            Text = model.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["ClipsyBody"],
            Opacity = installed ? 1.0 : 0.7,
        };
        Grid.SetColumn(nameBlock, 0);

        var sizeBlock = new TextBlock
        {
            Text = model.ApproxSize,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["ClipsyHelper"],
        };
        Grid.SetColumn(sizeBlock, 1);

        var progress = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Value = 0,
            Width = 84,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(progress, 2);

        var btn = new Button
        {
            Content = installed ? CreateTessDeleteContent() : Strings.Get("BtnInstall"),
            Style = (Style)Application.Current.Resources["ClipsyButtonGhost"],
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(btn, 3);

        grid.Children.Add(nameBlock);
        grid.Children.Add(sizeBlock);
        grid.Children.Add(progress);
        grid.Children.Add(btn);

        btn.Click += async (_, _) =>
        {
            if (PpOcrV5Service.IsModelInstalled(model.Key))
            {
                btn.IsEnabled = false;
                await PpOcrV5Service.DeleteModelAsync(model.Key);
                if (_windowClosed) return;
                var idx = PpOcrModelList.Children.IndexOf(grid);
                if (idx >= 0)
                    PpOcrModelList.Children[idx] = CreatePpOcrModelRow(model);
            }
            else
            {
                _ = DownloadPpOcrModelAsync(model, grid, btn, progress);
            }
        };

        return grid;
    }

    private async Task DownloadPpOcrModelAsync(
        PpOcrV5RecognizerSpec model,
        Grid row,
        Button btn,
        ProgressBar progressBar)
    {
        if (_ppOcrDownloadCts.TryGetValue(model.Key, out var existing))
        {
            existing.Cancel();
            _ppOcrDownloadCts.Remove(model.Key);
        }

        var cts = new CancellationTokenSource();
        _ppOcrDownloadCts[model.Key] = cts;

        btn.IsEnabled = false;
        btn.Content = Strings.Get("TessInstalling");
        progressBar.Visibility = Visibility.Visible;
        progressBar.Value = 0;

        try
        {
            var progress = new Progress<int>(value =>
            {
                if (!_windowClosed)
                    DispatcherQueue.TryEnqueue(() => progressBar.Value = value);
            });

            await PpOcrV5Service.DownloadModelAsync(model.Key, progress, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Clipsy] PP-OCRv5 model '{model.Key}' download failed: {ex.Message}");
            if (!_windowClosed)
                NotificationService.Error("ErrPpOcrDownload");
        }
        finally
        {
            if (_ppOcrDownloadCts.TryGetValue(model.Key, out var current) &&
                ReferenceEquals(current, cts))
                _ppOcrDownloadCts.Remove(model.Key);

            cts.Dispose();

            if (!_windowClosed)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    var idx = PpOcrModelList.Children.IndexOf(row);
                    if (idx >= 0)
                        PpOcrModelList.Children[idx] = CreatePpOcrModelRow(model);
                });
            }
        }
    }

    private void BuildTranslateLangDropdowns()
    {
        var prevFrom = SelectedComboTag(TranslateFromBox);
        var prevTo   = SelectedComboTag(TranslateToBox);

        TranslateFromBox.Items.Clear();
        TranslateToBox.Items.Clear();

        bool ru = Strings.Lang == "ru";

        TranslateFromBox.Items.Add(new ComboBoxItem
        {
            Content = Strings.Get("LangAutoDetect"),
            Tag = "auto",
        });
        TranslateToBox.Items.Add(new ComboBoxItem
        {
            Content = Strings.Get("LangUiDefault"),
            Tag = "ui",
        });

        foreach (var lang in TranslationService.LangCatalog)
        {
            var name = ru ? lang.Ru : lang.En;
            TranslateFromBox.Items.Add(new ComboBoxItem { Content = name, Tag = lang.Code });
            TranslateToBox.Items.Add(new ComboBoxItem   { Content = name, Tag = lang.Code });
        }

        SelectComboByTag(TranslateFromBox, string.IsNullOrEmpty(prevFrom) ? "auto" : prevFrom);
        SelectComboByTag(TranslateToBox,   string.IsNullOrEmpty(prevTo)   ? "ui"   : prevTo);
    }
}
