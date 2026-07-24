using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using IOPath = System.IO.Path;
using Clipsy.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Rect = Windows.Foundation.Rect;

namespace Clipsy.Views;

public sealed partial class CaptureOverlayWindow
{
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
        CaptureOverlayHost.Dismiss(this);
        await Task.Delay(150);
        dq.TryEnqueue(() => RecordingController.TryStart(x, y, w, h));
    }
    private void OnScreenshotClick(object sender, RoutedEventArgs e) => _ = SaveAsAsync();
    private void OnCopyClick(object sender, RoutedEventArgs e) => _ = CopyAsync();
    private void OnCancelClick(object sender, RoutedEventArgs e) => CloseDeferred();
    private async void OnOcrClick(object sender, RoutedEventArgs e) => await EnterOcrModeAsync();

    // ---------- Screenshot save / copy ----------

    // Closing while the context MenuFlyout is still tearing down crashes natively
    // (AV). Hide the flyout, cloak the window, and defer Close past the popup.
    private void CloseDeferred()
    {
        try { OverlayMenu.Hide(); } catch { }
        HideForClose();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => CaptureOverlayHost.Dismiss(this));
    }

    private async Task SaveSilentAsync()
    {
        if (!_hasSelection) return;
        try
        {
            var settings = SettingsService.Instance;
            var fmt = ScreenshotRenderer.ParseFormat(settings.Settings.ScreenshotFormat);
            var ext = ScreenshotRenderer.ExtensionFor(fmt);
            var folder = settings.GetEffectiveScreenshotFolder();
            Directory.CreateDirectory(folder);
            var name = SaveDialogService.MakeTimestampName("Clipsy", ext);
            var fullPath = IOPath.Combine(folder, name);
            var bytes = ScreenshotRenderer.RenderEncoded(_frame, _selectionRect, _drawing.Elements,
                DpiScale, fmt, settings.Settings.JpgQuality);
            await File.WriteAllBytesAsync(fullPath, bytes);
            NotificationService.ScreenshotSaved(name, bytes.LongLength / 1024L, fullPath);
            AfterSaveAction.Run(fullPath, settings.Settings.AfterSaveAction);
            CloseDeferred();
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
            var preferredFmt = ScreenshotRenderer.ParseFormat(settings.Settings.ScreenshotFormat);
            var preferredExt = ScreenshotRenderer.ExtensionFor(preferredFmt);
            var name = SaveDialogService.MakeTimestampName("Clipsy", preferredExt);

            var filters = new List<SaveDialogService.SaveFilter>
            {
                new("PNG image (*.png)",   "*.png"),
                new("JPEG image (*.jpg)",  "*.jpg"),
                new("WebP image (*.webp)", "*.webp"),
            };
            // Move the preferred format to the top so the dialog defaults to it.
            int preferredIdx = preferredFmt switch
            {
                ScreenshotRenderer.OutputFormat.Jpeg => 1,
                ScreenshotRenderer.OutputFormat.Webp => 2,
                _ => 0,
            };
            if (preferredIdx > 0)
            {
                var picked = filters[preferredIdx];
                filters.RemoveAt(preferredIdx);
                filters.Insert(0, picked);
            }

            var result = await SaveDialogService.PickSaveAsync(_hwnd, suggestedFolder, name, filters, preferredExt);
            if (result == null) return;

            // Figure out the format from the chosen filter; fall back to file extension.
            var chosen = filters[System.Math.Max(0, result.FilterIndex - 1)];
            var chosenExt = SaveDialogService.ExtensionFromPattern(chosen.Pattern);
            var pathExt = IOPath.GetExtension(result.Path);
            var finalExt = string.IsNullOrEmpty(pathExt) ? chosenExt : pathExt;
            var fmt = ScreenshotRenderer.ParseFormat(finalExt.TrimStart('.'));
            var finalPath = result.Path;
            if (string.IsNullOrEmpty(pathExt))
            {
                finalPath = result.Path + chosenExt;
            }

            var bytes = ScreenshotRenderer.RenderEncoded(_frame, _selectionRect, _drawing.Elements,
                DpiScale, fmt, settings.Settings.JpgQuality);
            await File.WriteAllBytesAsync(finalPath, bytes);
            NotificationService.ScreenshotSaved(IOPath.GetFileName(finalPath), bytes.LongLength / 1024L, finalPath);
            var dir = IOPath.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(dir))
            {
                settings.Settings.LastScreenshotFolder = dir;
                settings.Save();
            }
            AfterSaveAction.Run(finalPath, settings.Settings.AfterSaveAction);
            CloseDeferred();
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
            NotificationService.CopiedToClipboard();
            CloseDeferred();
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
    private void OnMenuCancel(object sender, RoutedEventArgs e) => CloseDeferred();
}
