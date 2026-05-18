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
    private RegionBorderWindow? _border;
    private RecordingHudWindow? _hud;
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

    private void Start(int x, int y, int w, int h)
    {
        _x = x; _y = y; _w = w; _h = h;

        _border = new RegionBorderWindow();
        _border.MoveTo(x, y, w, h);
        _border.Activate();

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
            Cleanup(discardTemp: true);
        }
    }

    private void OnPauseRequested() => _service?.Pause();
    private void OnResumeRequested() => _service?.Resume();

    private void OnStopRequested()
    {
        if (_stopping) return;
        _stopping = true;
        _stopAndSave = false;
        _service?.Stop();
    }

    private void OnStopSaveRequested()
    {
        if (_stopping) return;
        _stopping = true;
        _stopAndSave = true;
        _service?.Stop();
    }

    private void OnLockChanged(bool locked) { /* phase 5: visual only */ }
    private void OnDrawToggled(bool on) { /* drawing-during-recording deferred */ }

    private void OnRecordingComplete(string filePath)
    {
        _ui.TryEnqueue(async () =>
        {
            try
            {
                if (_stopAndSave)
                {
                    await OfferSaveAsync(filePath);
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

    private void OnRecordingFailed(string error)
    {
        Debug.WriteLine($"[Clipsy] Recording failed: {error}");
        _ui.TryEnqueue(() => Cleanup(discardTemp: true));
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
        var pick = await SaveDialogService.PickPngSaveAsync(hwnd, initialDir!, name);
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
            _border?.Close();
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
