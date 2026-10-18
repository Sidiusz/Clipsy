using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Clipsy.Drawing;
using WinRect = Windows.Foundation.Rect;

namespace Clipsy.Services;

/// <summary>Hotkey actions that capture without opening the overlay.</summary>
public static class QuickCaptureService
{
    private static int _screenshotBusy;

    /// <summary>Starts recording the window the user is in, or stops and saves the running recording.</summary>
    public static void ToggleRecordActiveWindow()
    {
        if (StopRunningRecording()) return;
        var target = WindowTargets.Foreground();
        if (target == null)
        {
            NotificationService.Info("InfoNoWindowToRecord");
            return;
        }
        RecordingController.TryStartWindow(target.Hwnd, quick: true);
    }

    /// <summary>Starts recording the monitor under the cursor, or stops and saves the running recording.</summary>
    public static void ToggleRecordScreenUnderCursor()
    {
        if (StopRunningRecording()) return;
        var monitor = ScreenFreezeService.MonitorUnderCursor(ScreenFreezeService.EnumerateMonitors());
        if (monitor == null) return;
        var b = monitor.Bounds;
        RecordingController.TryStart(b.X, b.Y, b.Width, b.Height, quick: true);
    }

    private static bool StopRunningRecording()
    {
        if (!RecordingController.IsRecording) return false;
        RecordingController.Current?.StopFromHotkey();
        return true;
    }

    /// <summary>Saves the monitor under the cursor to the screenshot folder and shows the saved toast.</summary>
    public static void ScreenshotUnderCursor()
    {
        if (Interlocked.Exchange(ref _screenshotBusy, 1) != 0) return;
        Task.Run(() =>
        {
            try
            {
                var settings = SettingsService.Instance;
                var frame = new ScreenFreezeService().Capture();
                var monitor = ScreenFreezeService.MonitorUnderCursor(frame.Monitors)
                    ?? throw new InvalidOperationException("No monitors are available.");
                var b = frame.VirtualBounds;
                var selection = new WinRect(monitor.Bounds.X - b.X, monitor.Bounds.Y - b.Y, monitor.Bounds.Width, monitor.Bounds.Height);
                var image = ScreenshotRenderer.Render(frame, selection, Array.Empty<DrawElement>(), 1.0);
                var fmt = ScreenshotRenderer.EffectiveFormat(ScreenshotRenderer.ParseFormat(settings.Settings.ScreenshotFormat), image.Width, image.Height);
                var bytes = ScreenshotRenderer.Encode(image.Bgra, image.Width, image.Height, fmt, settings.Settings.JpgQuality);
                var path = SaveDialogService.WriteNewFile(settings.GetEffectiveScreenshotFolder(), "Clipsy", ScreenshotRenderer.ExtensionFor(fmt), bytes);
                NotificationService.ScreenshotSaved(Path.GetFileName(path), bytes.LongLength / 1024L, path);
                AfterSaveAction.Run(path, settings.Settings.AfterSaveAction);
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Quick screenshot failed", ex);
                NotificationService.Error("ErrSaveFailed");
            }
            finally { Volatile.Write(ref _screenshotBusy, 0); }
        });
    }
}
