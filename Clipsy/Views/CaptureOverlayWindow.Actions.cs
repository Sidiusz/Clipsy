using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IOPath = System.IO.Path;
using Clipsy.Drawing;
using Clipsy.Localization;
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

    // Everything a background render needs, copied so the overlay can be reset or reused meanwhile.
    private sealed record RenderJob(ScreenFreezeService.FrozenFrame Frame, Rect Selection, DrawElement[] Elements, double Scale, int Session);

    private bool _busy;

    private RenderJob SnapshotRenderJob()
        => new(_frame, _selectionRect, _drawing.Elements.ToArray(), DpiScale, _session);

    // A save/copy that finishes after the user moved on must not close the next capture.
    private void CloseIfSameSession(RenderJob job)
    {
        if (job.Session == _session && !_closed) CloseDeferred();
    }

    private async Task SaveSilentAsync()
    {
        if (!_hasSelection || _busy) return;
        _busy = true;
        var job = SnapshotRenderJob();
        try
        {
            var settings = SettingsService.Instance;
            var fmt = ScreenshotRenderer.ParseFormat(settings.Settings.ScreenshotFormat);
            int quality = settings.Settings.JpgQuality;
            var folder = settings.GetEffectiveScreenshotFolder();
            var fullPath = await Task.Run(() =>
            {
                var bytes = ScreenshotRenderer.RenderEncoded(job.Frame, job.Selection, job.Elements, job.Scale, fmt, quality);
                return WriteUnique(folder, ScreenshotRenderer.ExtensionFor(fmt), bytes);
            });
            NotificationService.ScreenshotSaved(IOPath.GetFileName(fullPath), new FileInfo(fullPath).Length / 1024L, fullPath);
            AfterSaveAction.Run(fullPath, settings.Settings.AfterSaveAction);
            CloseIfSameSession(job);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Silent save failed", ex);
            NotificationService.Error("ErrSaveFailed");
        }
        finally { _busy = false; }
    }

    // CreateNew never overwrites: two saves in the same second get "_2", "_3"...
    private static string WriteUnique(string folder, string ext, byte[] bytes)
    {
        Directory.CreateDirectory(folder);
        var baseName = IOPath.GetFileNameWithoutExtension(SaveDialogService.MakeTimestampName("Clipsy", ext));
        for (int i = 1; ; i++)
        {
            var path = IOPath.Combine(folder, (i == 1 ? baseName : $"{baseName}_{i}") + ext);
            try
            {
                using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                fs.Write(bytes, 0, bytes.Length);
                return path;
            }
            catch (IOException) when (File.Exists(path) && i < 1000) { }
        }
    }

    private async Task SaveAsAsync()
    {
        if (!_hasSelection || _busy) return;
        _busy = true;
        var job = SnapshotRenderJob();
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
            if (result == null || job.Session != _session) return;

            // Figure out the format from the chosen filter; fall back to file extension.
            var chosen = filters[System.Math.Max(0, result.FilterIndex - 1)];
            var chosenExt = SaveDialogService.ExtensionFromPattern(chosen.Pattern);
            var pathExt = IOPath.GetExtension(result.Path);
            var finalExt = string.IsNullOrEmpty(pathExt) ? chosenExt : pathExt;
            var fmt = ScreenshotRenderer.ParseFormat(finalExt.TrimStart('.'));
            var finalPath = result.Path;
            if (string.IsNullOrEmpty(pathExt))
            {
                finalPath = result.Path + "." + chosenExt;
            }

            int quality = settings.Settings.JpgQuality;
            var bytes = await Task.Run(() =>
                ScreenshotRenderer.RenderEncoded(job.Frame, job.Selection, job.Elements, job.Scale, fmt, quality));
            await File.WriteAllBytesAsync(finalPath, bytes);
            NotificationService.ScreenshotSaved(IOPath.GetFileName(finalPath), bytes.LongLength / 1024L, finalPath);
            var dir = IOPath.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(dir))
            {
                settings.Settings.LastScreenshotFolder = dir;
                settings.SaveState();
            }
            AfterSaveAction.Run(finalPath, settings.Settings.AfterSaveAction);
            CloseIfSameSession(job);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Save As failed", ex);
            NotificationService.Error("ErrSaveFailed");
        }
        finally { _busy = false; }
    }

    private async Task CopyAsync()
    {
        if (!_hasSelection || _busy) return;
        _busy = true;
        var job = SnapshotRenderJob();
        try
        {
            var png = await Task.Run(() => ScreenshotRenderer.RenderPng(job.Frame, job.Selection, job.Elements, job.Scale));
            await ClipboardService.SetImageAsync(png);
            NotificationService.CopiedToClipboard();
            CloseIfSameSession(job);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Copy failed", ex);
            NotificationService.Error("ErrCopyFailed");
        }
        finally { _busy = false; }
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
                Text = string.Format(Strings.Get(m.IsPrimary ? "MenuScreenPrimary" : "MenuScreenN"), i),
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
            SelectMonitor(m);
    }

    private void OnMenuSelectAll(object sender, RoutedEventArgs e) => SelectAll();
    private void OnMenuCopy(object sender, RoutedEventArgs e) => _ = CopyAsync();
    private void OnMenuSave(object sender, RoutedEventArgs e) => _ = SaveSilentAsync();
    private void OnMenuSaveAs(object sender, RoutedEventArgs e) => _ = SaveAsAsync();
    // Removes annotations only (undoable); the selection and active tool stay.
    private void OnMenuClear(object sender, RoutedEventArgs e) => _drawing.ClearDrawings();
    private void OnMenuCancel(object sender, RoutedEventArgs e) => CloseDeferred();
}
