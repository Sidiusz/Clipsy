using System;
using System.Threading;
using System.Threading.Tasks;
using Clipsy.Localization;
using Clipsy.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Clipsy.Views.Settings;

public sealed partial class SettingsWindow
{
    private void OnOcrEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTessLangSectionVisibility();
        MarkChanged();
    }

    private void UpdateTessLangSectionVisibility()
    {
        if (TessLangSection == null) return;
        var isTesseract = string.Equals(SelectedComboTag(OcrEngineBox), "Tesseract", StringComparison.OrdinalIgnoreCase);
        TessLangSection.Visibility = isTesseract ? Visibility.Visible : Visibility.Collapsed;
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

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // One row, one action (Install/Delete): an installed language is used
        // automatically, so there's no separate "selected" checkbox.
        CheckBox? cb = null; // signature compatibility with DownloadTessLangAsync
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
            Style = (Style)Application.Current.Resources["ClipsyHelper"],
            Margin = new Thickness(8, 0, 8, 0),
        };
        Grid.SetColumn(sizeBlock, 1);

        var progress = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Value = 0,
            Width = 80,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(progress, 2);

        var btn = new Button
        {
            Content = installed ? Strings.Get("BtnDelete") : Strings.Get("BtnInstall"),
            Style = (Style)Application.Current.Resources[installed ? "ClipsyButtonGhost" : "ClipsyButtonGhost"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
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
                TessdataService.Delete(lang.Code);
                _tessSelectedCodes.Remove(lang.Code);
                MarkChanged();
                var idx = TessLangList.Children.IndexOf(grid);
                if (idx >= 0) TessLangList.Children[idx] = CreateTessLangRow(lang);
            }
            else
            {
                _ = DownloadTessLangAsync(lang, grid, btn, progress, cb);
            }
        };

        return grid;
    }

    private async Task DownloadTessLangAsync(TessdataLang lang, Grid row, Button btn, ProgressBar progressBar, CheckBox? cb)
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
            var p = new Progress<int>(v => DispatcherQueue.TryEnqueue(() => progressBar.Value = v));
            await TessdataService.DownloadAsync(lang.Code, p, cts.Token);

            _tessSelectedCodes.Add(lang.Code);
            MarkChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Tessdata download failed: {ex.Message}");
            NotificationService.Error("ErrTessDownload");
        }
        finally
        {
            _tessDownloadCts.Remove(lang.Code);
            DispatcherQueue.TryEnqueue(() =>
            {
                var idx = TessLangList.Children.IndexOf(row);
                if (idx >= 0) TessLangList.Children[idx] = CreateTessLangRow(lang);
            });
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
