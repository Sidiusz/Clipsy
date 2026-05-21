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
    private RecordingDrawingWindow? _drawWin;
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
        _hud.MoveDeltaRequested += OnMoveDelta;

        int virtualScreenH = Services.ScreenFreezeService.GetVirtualScreenBounds().Height;
        _hud.PositionBelowRegion(x, y, w, h, virtualScreenH);
        _hud.Activate();
        _hud.Start();

        // Hide both Clipsy windows from the capture so the red ring and the
        // HUD never appear in the recorded MP4.
        try
        {
            // Win32BorderOverlay doesn't need exclude from capture
            Recorder.SetExcludeFromCapture(_hud.Hwnd, true);
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

    private void OnMoveDelta(int dx, int dy)
    {
        _x += dx; _y += dy;
        ApplyRegionChange(_x, _y, _w, _h);
    }

    private void OnPauseRequested() => _service?.Pause();
    private void OnResumeRequested() => _service?.Resume();

    private bool _saveAsDialog;

    /// <summary>Stop button: silent save to the last/default video folder.</summary>
    private void OnStopRequested()
    {
        if (_stopping) return;
        _stopping = true;
        _stopAndSave = true;
        _saveAsDialog = false;
        _hud?.Shutdown();
        _service?.Stop();
    }

    /// <summary>Save button: stop then open a Save As dialog.</summary>
    private void OnStopSaveRequested()
    {
        if (_stopping) return;
        _stopping = true;
        _stopAndSave = true;
        _saveAsDialog = true;
        _hud?.Shutdown();
        _service?.Stop();
    }

    private void OnLockChanged(bool locked)
    {
        try
        {
            // Win32BorderOverlay is not interactive
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
                    _drawWin = new RecordingDrawingWindow();
                    _drawWin.MoveTo(_x, _y, _w, _h);
                    _drawWin.Activate();
                    _drawWin.SetColor(Microsoft.UI.Colors.Red);
                    _drawWin.SetThickness(3.0);
                }
                _drawWin.SetActive(true);
            }
            else
            {
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
        _ui.TryEnqueue(async () =>
        {
            try
            {
                if (_stopAndSave)
                {
                    if (_saveAsDialog) await OfferSaveAsync(filePath);
                    else SilentSave(filePath);
                }
                else
                {
                    TryDelete(filePath);
                }
            }
            finally
            {
                Cleanup(discardTemp: false);
            }
        });
    }

    private void SilentSave(string tempPath)
    {
        try
        {
            var settings = SettingsService.Instance;
            var folder = settings.Settings.RememberLastFolder && !string.IsNullOrEmpty(settings.Settings.LastVideoFolder)
                ? settings.Settings.LastVideoFolder!
                : (settings.Settings.VideoFolder ?? settings.DefaultVideoFolder);
            Directory.CreateDirectory(folder);
            var name = SaveDialogService.MakeTimestampName("Clipsy", "mp4");
            var dest = Path.Combine(folder, name);
            File.Copy(tempPath, dest, overwrite: true);
            TryDelete(tempPath);
            settings.Settings.LastVideoFolder = folder;
            settings.Save();
            AfterSaveAction.Run(dest, settings.Settings.AfterSaveAction);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipsy] Silent recording save failed: {ex.Message}");
            NotificationService.Error("ErrSaveFailed");
        }
    }

    private void OnRecordingFailed(string error)
    {
        Debug.WriteLine($"[Clipsy] Recording failed: {error}");
        _ui.TryEnqueue(() =>
        {
            NotificationService.Error("ErrRecordRuntime");
            Cleanup(discardTemp: true);
        });
    }

    private async Task OfferSaveAsync(string tempPath)
    {
        var settings = SettingsService.Instance;
        var initialDir = settings.Settings.RememberLastFolder && !string.IsNullOrEmpty(settings.Settings.LastVideoFolder)
            ? settings.Settings.LastVideoFolder!
            : (settings.Settings.VideoFolder ?? settings.DefaultVideoFolder);
        Directory.CreateDirectory(initialDir);
        var name = SaveDialogService.MakeTimestampName("Clipsy", "mp4");
        var hwnd = _hud != null ? WinRT.Interop.WindowNative.GetWindowHandle(_hud) : IntPtr.Zero;
        var filters = new System.Collections.Generic.List<SaveDialogService.SaveFilter>
        {
            new("MP4 video (*.mp4)", "*.mp4"),
        };
        var pick = await SaveDialogService.PickSaveAsync(hwnd, initialDir!, name, filters, ".mp4");
        if (pick == null)
        {
            TryDelete(tempPath);
            return;
        }
        var dest = pick.Path;
        if (!dest.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            dest = Path.ChangeExtension(dest, ".mp4");
        }
        try
        {
            File.Copy(tempPath, dest, overwrite: true);
            TryDelete(tempPath);
            var dir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(dir))
            {
                settings.Settings.LastVideoFolder = dir;
                settings.Save();
            }
            AfterSaveAction.Run(dest, settings.Settings.AfterSaveAction);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipsy] Move recording failed: {ex.Message}");
        }
    }

    private void Cleanup(bool discardTemp)
    {
        try
        {
            _hud?.Shutdown();
            _hud?.Close();
            _border?.Destroy();
            _drawWin?.Close();
            _service?.Dispose();
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
            _service = null;
            _stopping = false;
            _current = null;
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
