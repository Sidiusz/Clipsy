using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Clipsy.Views.Recording;
using Microsoft.UI.Dispatching;
using ScreenRecorderLib;

namespace Clipsy.Services;

/// <summary>
/// Owns the recording session: shows the red region border + HUD, drives
/// the RecordingService, and handles stop / stop-and-save flows. Singleton
/// per app run since only one recording can be active at a time.
/// </summary>
public sealed class RecordingController
{
    private static RecordingController? _current;
    public static RecordingController? Current => _current;

    public static bool IsRecording => _current != null;

    private readonly DispatcherQueue _ui;
    private Win32BorderOverlay? _border;
    private RecordingHudWindow? _hud;
    private Win32DrawingOverlay? _drawWin;
    private Win32ResizeOverlay? _resizeWin;
    private RecordingService? _service;
    private int _x, _y, _w, _h;
    private bool _stopAndSave;
    private bool _stopping;

    private RecordingController(DispatcherQueue ui) { _ui = ui; }

    public static bool TryStart(int x, int y, int w, int h)
    {
        if (_current != null) return false;
        var ui = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Recording must be started from the UI thread.");
        var c = new RecordingController(ui);
        c.Start(x, y, w, h);
        _current = c;
        return true;
    }

    public void StopFromHotkey()
    {
        if (_stopping) return;
        _stopping = true;
        _stopAndSave = true;
        _saveAsDialog = false;
        _hud?.Shutdown();
        _service?.Stop();
    }

    private void Start(int x, int y, int w, int h)
    {
        _x = x; _y = y; _w = w; _h = h;

        _border = new Win32BorderOverlay();
        _border.Create(x, y, w, h);

        _hud = new RecordingHudWindow();
        _hud.PauseRequested += OnPauseRequested;
        _hud.ResumeRequested += OnResumeRequested;
        _hud.StopRequested += OnStopRequested;
        _hud.StopSaveRequested += OnStopSaveRequested;
        _hud.LockChanged += OnLockChanged;
        _hud.DrawToggled += OnDrawToggled;

        int virtualScreenH = Services.ScreenFreezeService.GetVirtualScreenBounds().Height;
        _hud.PositionBelowRegion(x, y, w, h, virtualScreenH);
        _hud.Activate();
        _hud.Start();

        // Exclude Clipsy overlay windows from the recorded MP4 so the HUD,
        // region border, and any draw overlay don't appear in the output.
        try
        {
            Recorder.SetExcludeFromCapture(_hud.Hwnd, true);
            if (_border != null && _border.Hwnd != IntPtr.Zero)
                Recorder.SetExcludeFromCapture(_border.Hwnd, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipsy] SetExcludeFromCapture failed: {ex.Message}");
        }

        _service = new RecordingService();
        _service.RecordingComplete += OnRecordingComplete;
        _service.RecordingFailed += OnRecordingFailed;
        try
        {
            _service.Start(x, y, w, h);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipsy] Recording start failed: {ex.Message}");
            NotificationService.Error("ErrRecordFailed");
            Cleanup(discardTemp: true);
        }
    }

    private void OnPauseRequested() => _service?.Pause();
    private void OnResumeRequested() => _service?.Resume();

    private bool _saveAsDialog;
    private IntPtr _hudHwnd; // captured before Shutdown so OfferSaveAsync has a valid owner

    /// <summary>Stop button: silent save to the last/default video folder.</summary>
    private void OnStopRequested()
    {
        Diagnostics.Log("RecordingController.OnStopRequested ENTER");
        if (_stopping) { Diagnostics.Log("  already _stopping, skip"); return; }
        _stopping = true;
        _stopAndSave = true;
        _saveAsDialog = false;
        _hudHwnd = _hud?.Hwnd ?? IntPtr.Zero;
        Diagnostics.Log($"  captured _hudHwnd=0x{_hudHwnd.ToInt64():X}");
        try { _hud?.Shutdown(); Diagnostics.Log("  _hud.Shutdown OK"); }
        catch (Exception ex) { Diagnostics.Log("OnStopRequested _hud.Shutdown", ex); }
        try { _service?.Stop(); Diagnostics.Log("  _service.Stop OK"); }
        catch (Exception ex) { Diagnostics.Log("OnStopRequested _service.Stop", ex); }
    }

    /// <summary>Save button: stop then open a Save As dialog.</summary>
    private void OnStopSaveRequested()
    {
        Diagnostics.Log("RecordingController.OnStopSaveRequested ENTER");
        if (_stopping) { Diagnostics.Log("  already _stopping, skip"); return; }
        _stopping = true;
        _stopAndSave = true;
        _saveAsDialog = true;
        _hudHwnd = _hud?.Hwnd ?? IntPtr.Zero;
        Diagnostics.Log($"  captured _hudHwnd=0x{_hudHwnd.ToInt64():X}");
        try { _hud?.Shutdown(); Diagnostics.Log("  _hud.Shutdown OK"); }
        catch (Exception ex) { Diagnostics.Log("OnStopSaveRequested _hud.Shutdown", ex); }
        try { _service?.Stop(); Diagnostics.Log("  _service.Stop OK"); }
        catch (Exception ex) { Diagnostics.Log("OnStopSaveRequested _service.Stop", ex); }
    }

    private void OnLockChanged(bool locked)
    {
        try
        {
            if (!locked)
            {
                if (_resizeWin == null)
                {
                    _resizeWin = new Win32ResizeOverlay();
                    _resizeWin.RegionChanged += OnRegionChanged;
                    _resizeWin.Create(_x, _y, _w, _h);
                    try { Recorder.SetExcludeFromCapture(_resizeWin.Hwnd, true); } catch { }
                }
                _resizeWin.MoveTo(_x, _y, _w, _h);
            }
            else
            {
                _resizeWin?.Destroy();
                _resizeWin = null;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipsy] Region lock toggle failed: {ex.Message}");
        }
    }

    private void OnRegionChanged(int x, int y, int w, int h)
    {
        ApplyRegionChange(x, y, w, h);
    }

    private void ApplyRegionChange(int x, int y, int w, int h)
    {
        _x = x;
        _y = y;
        _w = w;
        _h = h;
        try
        {
            _border?.MoveTo(_x, _y, _w, _h);
            var screenH = ScreenFreezeService.GetVirtualScreenBounds().Height;
            _hud?.PositionBelowRegion(_x, _y, _w, _h, screenH);
            _drawWin?.MoveTo(_x, _y, _w, _h);
            _resizeWin?.MoveTo(_x, _y, _w, _h);
            _service?.UpdateRegion(_x, _y, _w, _h);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipsy] Region update failed: {ex.Message}");
        }
    }

    private void OnDrawToggled(bool on)
    {
        try
        {
            if (on)
            {
                if (_drawWin == null)
                {
                    _drawWin = new Win32DrawingOverlay();
                    _drawWin.Create(_x, _y, _w, _h);
                    try { Recorder.SetExcludeFromCapture(_drawWin.Hwnd, false); } catch { }
                }
                _drawWin.SetActive(true);
            }
            else
            {
                // Keep existing strokes visible. User asked for explicit clear,
                // not auto-wipe on toggle. RMB-drag erases; full clear requires
                // a dedicated UI hook (TODO).
                _drawWin?.SetActive(false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipsy] Draw overlay toggle failed: {ex.Message}");
        }
    }

    private void OnRecordingComplete(string filePath)
    {
        Diagnostics.Log($"RecordingController.OnRecordingComplete fired: filePath='{filePath}', exists={System.IO.File.Exists(filePath)}, _stopAndSave={_stopAndSave}, _saveAsDialog={_saveAsDialog}");
        _ui.TryEnqueue(async () =>
        {
            Diagnostics.Log("OnRecordingComplete UI continuation BEGIN");
            try
            {
                if (_stopAndSave)
                {
                    if (_saveAsDialog)
                    {
                        Diagnostics.Log("  → OfferSaveAsync");
                        await OfferSaveAsync(filePath);
                        Diagnostics.Log("  ← OfferSaveAsync returned");
                    }
                    else
                    {
                        Diagnostics.Log("  → SilentSave");
                        SilentSave(filePath);
                        Diagnostics.Log("  ← SilentSave returned");
                    }
                }
                else
                {
                    Diagnostics.Log("  → discard temp");
                    TryDelete(filePath);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("OnRecordingComplete continuation", ex);
            }
            finally
            {
                Diagnostics.Log("OnRecordingComplete → Cleanup");
                try { Cleanup(discardTemp: false); Diagnostics.Log("Cleanup OK"); }
                catch (Exception ex) { Diagnostics.Log("Cleanup", ex); }
            }
        });
    }

    private async void SilentSave(string tempPath)
    {
        Diagnostics.Log($"SilentSave ENTER tempPath='{tempPath}'");
        try
        {
            var settings = SettingsService.Instance;
            var folder = settings.Settings.RememberLastFolder && !string.IsNullOrEmpty(settings.Settings.LastVideoFolder)
                ? settings.Settings.LastVideoFolder!
                : (settings.Settings.VideoFolder ?? settings.DefaultVideoFolder);
            Diagnostics.Log($"  folder='{folder}'");
            Directory.CreateDirectory(folder);

            var format = settings.Settings.VideoFormat;
            var name = SaveDialogService.MakeTimestampName("Clipsy", format);
            var dest = Path.Combine(folder, name);
            Diagnostics.Log($"  dest='{dest}', format='{format}'");

            if (format == "mp4")
            {
                File.Copy(tempPath, dest, overwrite: true);
            }
            else
            {
                var success = await NativeVideoConverter.ConvertToFormatAsync(tempPath, dest, format);
                if (!success)
                {
                    // Fallback to MP4 if conversion fails
                    dest = Path.ChangeExtension(dest, ".mp4");
                    File.Copy(tempPath, dest, overwrite: true);
                    NotificationService.Warning($"Conversion to {format.ToUpper()} failed - saved as MP4");
                }
            }

            Diagnostics.Log("  File conversion/copy OK");
            TryDelete(tempPath);
            settings.Settings.LastVideoFolder = folder;
            settings.Save();
            Diagnostics.Log($"  AfterSaveAction.Run action='{settings.Settings.AfterSaveAction}'");
            AfterSaveAction.Run(dest, settings.Settings.AfterSaveAction);
            Diagnostics.Log("SilentSave EXIT OK");
        }
        catch (Exception ex)
        {
            Diagnostics.Log("SilentSave", ex);
            NotificationService.Error("ErrSaveFailed");
        }
    }

    private void OnRecordingFailed(string error)
    {
        Diagnostics.Log($"RecordingController.OnRecordingFailed: {error}");
        _ui.TryEnqueue(() =>
        {
            NotificationService.Error("ErrRecordRuntime");
            try { Cleanup(discardTemp: true); }
            catch (Exception ex) { Diagnostics.Log("OnRecordingFailed Cleanup", ex); }
        });
    }

    private async Task OfferSaveAsync(string tempPath)
    {
        Diagnostics.Log($"OfferSaveAsync ENTER tempPath='{tempPath}'");
        var settings = SettingsService.Instance;
        var initialDir = settings.Settings.RememberLastFolder && !string.IsNullOrEmpty(settings.Settings.LastVideoFolder)
            ? settings.Settings.LastVideoFolder!
            : (settings.Settings.VideoFolder ?? settings.DefaultVideoFolder);
        Directory.CreateDirectory(initialDir);

        var defaultFormat = settings.Settings.VideoFormat;
        var name = SaveDialogService.MakeTimestampName("Clipsy", defaultFormat);

        // Prefer HostWindow over HUD hwnd: HUD is a TOOLWINDOW + NOACTIVATE +
        // TRANSPARENT click-through window — invalid modal owner for common dialogs.
        var hwnd = App.Current?.HostWindow?.Hwnd ?? _hudHwnd;

        var filters = new System.Collections.Generic.List<SaveDialogService.SaveFilter>
        {
            new("MP4 video (*.mp4)", "*.mp4"),
            new("AVI video (*.avi)", "*.avi"),
            new("MKV video (*.mkv)", "*.mkv"),
            new("GIF animation (*.gif)", "*.gif"),
        };

        // Set default extension based on current format setting
        var defaultExt = "." + defaultFormat;

        Diagnostics.Log($"  hwnd=0x{hwnd.ToInt64():X}, initialDir='{initialDir}', suggested='{name}'");

        // Hide any remaining overlays during save dialog to prevent interference
        try
        {
            _border?.SetZOrder(false);
            _drawWin?.SetZOrder(false);
            _resizeWin?.SetZOrder(false);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("OfferSaveAsync overlay z-order", ex);
        }

        SaveDialogService.SavePickResult? pick = null;
        try
        {
            pick = await SaveDialogService.PickSaveAsync(hwnd, initialDir!, name, filters, defaultExt);
            Diagnostics.Log($"  PickSaveAsync returned pick={(pick == null ? "null" : "'" + pick.Path + "'")}");
        }
        catch (Exception ex)
        {
            Diagnostics.Log("OfferSaveAsync PickSaveAsync", ex);
        }
        if (pick == null)
        {
            TryDelete(tempPath);
            Diagnostics.Log("OfferSaveAsync EXIT (no pick)");
            return;
        }

        var dest = pick.Path;
        var selectedFormat = SaveDialogService.ExtensionFromPattern(filters[pick.FilterIndex - 1].Pattern);

        // Ensure correct extension
        if (!dest.EndsWith($".{selectedFormat}", StringComparison.OrdinalIgnoreCase))
        {
            dest = Path.ChangeExtension(dest, selectedFormat);
        }

        try
        {
            Diagnostics.Log($"  Converting to format '{selectedFormat}' → '{dest}'");

            if (selectedFormat == "mp4")
            {
                File.Copy(tempPath, dest, overwrite: true);
            }
            else
            {
                var success = await NativeVideoConverter.ConvertToFormatAsync(tempPath, dest, selectedFormat);
                if (!success)
                {
                    // Fallback to MP4 if conversion fails
                    dest = Path.ChangeExtension(dest, ".mp4");
                    File.Copy(tempPath, dest, overwrite: true);
                    NotificationService.Warning($"Conversion to {selectedFormat.ToUpper()} failed - saved as MP4");
                }
            }

            TryDelete(tempPath);
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
            {
                settings.Settings.LastVideoFolder = dir;
                settings.Save();
            }
            Diagnostics.Log($"  AfterSaveAction.Run action='{settings.Settings.AfterSaveAction}'");
            AfterSaveAction.Run(dest, settings.Settings.AfterSaveAction);
            Diagnostics.Log("OfferSaveAsync EXIT OK");
        }
        catch (Exception ex)
        {
            Diagnostics.Log("OfferSaveAsync File.Copy", ex);
        }
    }

    private void Cleanup(bool discardTemp)
    {
        Diagnostics.Log($"Cleanup ENTER discardTemp={discardTemp}");
        try
        {
            try { _hud?.Shutdown(); Diagnostics.Log("  hud.Shutdown OK"); } catch (Exception ex) { Diagnostics.Log("Cleanup hud.Shutdown", ex); }
            try { _hud?.Close(); Diagnostics.Log("  hud.Close OK"); } catch (Exception ex) { Diagnostics.Log("Cleanup hud.Close", ex); }
            try { _border?.Destroy(); Diagnostics.Log("  border.Destroy OK"); } catch (Exception ex) { Diagnostics.Log("Cleanup border.Destroy", ex); }
            try { _drawWin?.Destroy(); Diagnostics.Log("  drawWin.Destroy OK"); } catch (Exception ex) { Diagnostics.Log("Cleanup drawWin.Destroy", ex); }
            try { _resizeWin?.Destroy(); Diagnostics.Log("  resizeWin.Destroy OK"); } catch (Exception ex) { Diagnostics.Log("Cleanup resizeWin.Destroy", ex); }
            try { _service?.Dispose(); Diagnostics.Log("  service.Dispose OK"); } catch (Exception ex) { Diagnostics.Log("Cleanup service.Dispose", ex); }
            if (discardTemp && _service != null && !string.IsNullOrEmpty(_service.TempPath))
            {
                TryDelete(_service.TempPath);
            }
        }
        finally
        {
            _border = null;
            _hud = null;
            _drawWin = null;
            _resizeWin = null;
            _service = null;
            _stopping = false;
            _current = null;
            Diagnostics.Log("Cleanup EXIT");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* ignore */ }
    }
}
