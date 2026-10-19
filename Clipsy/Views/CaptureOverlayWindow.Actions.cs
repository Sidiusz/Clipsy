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
        if (_selectedWindow is { Hwnd: var hwnd } && WindowTargets.Exists(hwnd))
        {
            var ui = App.Current.HostWindow!.DispatcherQueue;
            CaptureOverlayHost.Dismiss(this);
            await Task.Delay(150);
            ui.TryEnqueue(() => RecordingController.TryStartWindow(hwnd));
            return;
        }
        // Clamped to the frame: the recorder can't capture outside the virtual screen.
        var px = ScreenshotRenderer.ToPixelRect(_selectionRect, DpiScale, _frame.PixelWidth, _frame.PixelHeight);
        var b = _frame.VirtualBounds;
        int x = b.X + px.X, y = b.Y + px.Y, w = px.Width, h = px.Height;
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
        // Free the host now: a capture hotkey pressed before the deferred reset runs must open
        // a new session, not re-activate this cloaked window.
        CaptureOverlayHost.Detach(this);
        // Only this session: a second request (Esc auto-repeat) queued before the first ran
        // must not dismiss the next capture, which reuses this window.
        int session = _session;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (session == _session) CaptureOverlayHost.Dismiss(this);
        });
    }

    // Everything a background render needs, copied so the overlay can be reset or reused meanwhile.
    private sealed record RenderJob(ScreenFreezeService.FrozenFrame Frame, Rect Selection, DrawElement[] Elements, double Scale, int Session);

    // Session of the save/copy in progress: a slow one from a closed session must not block
    // the next capture, and its finally must not clear the next one's flag.
    private int _busySession = -1;

    private bool TryBeginBusy()
    {
        if (!_hasSelection || _busySession == _session) return false;
        _busySession = _session;
        return true;
    }

    private void EndBusy(RenderJob job)
    {
        if (_busySession == job.Session) _busySession = -1;
    }

    // Deep copies: the move tool and undo mutate elements in place while the render runs.
    private RenderJob SnapshotRenderJob()
        => new(_frame, _selectionRect, _drawing.Elements.Select(e => e.Clone()).ToArray(), DpiScale, _session);

    // WebP can't hold a side over 16383 px; such a render is written as PNG instead.
    private static (byte[] Bytes, ScreenshotRenderer.OutputFormat Format) RenderForSave(
        RenderJob job, ScreenshotRenderer.OutputFormat fmt, int quality)
    {
        var image = ScreenshotRenderer.Render(job.Frame, job.Selection, job.Elements, job.Scale);
        var actual = ScreenshotRenderer.EffectiveFormat(fmt, image.Width, image.Height);
        return (ScreenshotRenderer.Encode(image.Bgra, image.Width, image.Height, actual, quality), actual);
    }

    // A save/copy that finishes after the user moved on must not close the next capture.
    private void CloseIfSameSession(RenderJob job)
    {
        if (job.Session == _session && !_closed) CloseDeferred();
    }

    private async Task SaveSilentAsync()
    {
        if (!TryBeginBusy()) return;
        var job = SnapshotRenderJob();
        try
        {
            var settings = SettingsService.Instance;
            var fmt = ScreenshotRenderer.ParseFormat(settings.Settings.ScreenshotFormat);
            int quality = settings.Settings.JpgQuality;
            var folder = settings.GetEffectiveScreenshotFolder();
            var fullPath = await Task.Run(() =>
            {
                var (bytes, actual) = RenderForSave(job, fmt, quality);
                return SaveDialogService.WriteNewFile(folder, "Clipsy", ScreenshotRenderer.ExtensionFor(actual), bytes);
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
        finally { EndBusy(job); }
    }

    private async Task SaveAsAsync()
    {
        if (!TryBeginBusy()) return;
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

            // The typed extension picks the format; with none, or one we can't write (it
            // would hold PNG bytes under a foreign name), the chosen filter's is appended.
            var chosen = filters[System.Math.Max(0, result.FilterIndex - 1)];
            var chosenExt = SaveDialogService.ExtensionFromPattern(chosen.Pattern);
            var pathExt = IOPath.GetExtension(result.Path).TrimStart('.').ToLowerInvariant();
            bool knownExt = pathExt is "png" or "jpg" or "jpeg" or "webp";
            var fmt = ScreenshotRenderer.ParseFormat(knownExt ? pathExt : chosenExt);
            var finalPath = knownExt ? result.Path : result.Path + "." + chosenExt;

            int quality = settings.Settings.JpgQuality;
            var (bytes, actual) = await Task.Run(() => RenderForSave(job, fmt, quality));
            if (actual != fmt) finalPath = IOPath.ChangeExtension(finalPath, ScreenshotRenderer.ExtensionFor(actual));
            // Temp file + rename: a failed write never leaves the file being replaced truncated.
            await Task.Run(() => SaveDialogService.WriteReplacing(finalPath, bytes));
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
        finally { EndBusy(job); }
    }

    private async Task CopyAsync()
    {
        if (!TryBeginBusy()) return;
        var job = SnapshotRenderJob();
        try
        {
            await Task.Run(() => ClipboardService.SetImage(
                ScreenshotRenderer.Render(job.Frame, job.Selection, job.Elements, job.Scale)));
            NotificationService.CopiedToClipboard();
            CloseIfSameSession(job);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Copy failed", ex);
            NotificationService.Error("ErrCopyFailed");
        }
        finally { EndBusy(job); }
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
            AddListItem(SelectScreenMenu, item);
            i++;
        }
        BuildWindowMenu();
    }

    // Alternate rows so long lists of similar titles don't run together.
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush s_stripe = new(Windows.UI.Color.FromArgb(0x38, 0x80, 0x80, 0x80));

    private static void AddListItem(MenuFlyoutSubItem list, MenuFlyoutItem item)
    {
        if (list.Items.Count % 2 == 1) item.Background = s_stripe;
        ResetHoverOnExit(item);
        list.Items.Add(item);
    }

    // A hovered menu item takes keyboard focus and keeps looking hovered after the pointer leaves.
    private static void ResetHoverOnExit(MenuFlyoutItemBase item)
        => item.PointerExited += (s, _) => VisualStateManager.GoToState((Control)s, "Normal", true);

    private void HookMenuHoverReset()
    {
        foreach (var item in OverlayMenu.Items)
            if (item is MenuFlyoutItem or MenuFlyoutSubItem) ResetHoverOnExit(item);
        // Which items show depends on the selection, so the stripes are laid out on every open.
        OverlayMenu.Opening += (_, _) => StripeVisibleItems();
    }

    private void StripeVisibleItems()
    {
        int row = 0;
        foreach (var item in OverlayMenu.Items)
        {
            if (item.Visibility != Visibility.Visible) continue;
            if (item is MenuFlyoutSeparator) { row = 0; continue; }
            if (item is not Control c) continue;
            if (row++ % 2 == 1) c.Background = s_stripe;
            else c.ClearValue(Control.BackgroundProperty);
        }
    }

    private void BuildWindowMenu()
    {
        SelectWindowMenu.Items.Clear();
        foreach (var w in _frame.Windows)
        {
            var title = w.Title.Length > 60 ? w.Title[..57] + "…" : w.Title;
            var item = new MenuFlyoutItem { Text = string.Format(Strings.Get("MenuWindowItem"), title, w.Pid), Tag = w };
            ToolTipService.SetToolTip(item, w.Title);
            item.Click += OnMenuSelectWindow;
            AddListItem(SelectWindowMenu, item);
        }
        SelectWindowMenu.Visibility = _frame.Windows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMenuSelectWindow(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: WindowTargets.Target w }) SelectWindow(w);
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
        StripeVisibleItems();
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
