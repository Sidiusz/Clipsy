using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Clipsy.Views.Recording;
using Microsoft.UI.Dispatching;
using ScreenRecorderLib;

namespace Clipsy.Services;

/// <summary>Owns the recording session (region border + HUD, RecordingService,
/// stop/stop-and-save). Singleton — only one recording at a time.</summary>
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
    private FFmpegRecordingService? _ffmpegRec;
    private bool _h265FallbackAttempted;
    private string _outputFmt = "mp4";  // user-chosen container for the final file
    private string _nativeFmt = "mp4";  // actual container of the temp recording
    private int _x, _y, _w, _h;
    private bool _stopAndSave;
    private bool _stopping;
    private bool _micMuted = false;
    private (byte R, byte G, byte B) _drawColor = (0xFF, 0x00, 0x00);

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
        DestroyVisualOverlays();
        _service?.Stop();
        _ffmpegRec?.Stop();
    }

    // Tear down the topmost border + draw overlay the moment recording stops,
    // else they linger over the Save As dialog until Cleanup runs much later.
    private void DestroyVisualOverlays()
    {
        try { _border?.Destroy(); } catch (Exception ex) { Diagnostics.Log("DestroyVisualOverlays border", ex); }
        try { _drawWin?.Destroy(); } catch (Exception ex) { Diagnostics.Log("DestroyVisualOverlays drawWin", ex); }
        try { _resizeWin?.Destroy(); } catch (Exception ex) { Diagnostics.Log("DestroyVisualOverlays resizeWin", ex); }
        _border = null;
        _drawWin = null;
        _resizeWin = null;
    }

    private void Start(int x, int y, int w, int h)
    {
        _x = x; _y = y; _w = w; _h = h;

        // Determine output format before starting
        var settings = SettingsService.Instance.Settings;
        var codec = settings.VideoCodec;
        bool isFfmpegCodec = codec == "VP9" || codec == "AV1";
        // Native container of the temp file (what the encoder actually writes).
        _nativeFmt = isFfmpegCodec ? "mkv" : "mp4";
        // Container the user wants on disk. For ffmpeg codecs we always stay
        // in MKV; for H.264/H.265 we honour the format setting (mp4/avi/mkv/gif).
        _outputFmt = isFfmpegCodec ? "mkv" : (settings.VideoFormat ?? "mp4");

        _border = new Win32BorderOverlay();
        _border.Create(x, y, w, h);

        _hud = new RecordingHudWindow();
        _hud.PauseRequested += OnPauseRequested;
        _hud.ResumeRequested += OnResumeRequested;
        _hud.StopRequested += OnStopRequested;
        _hud.StopSaveRequested += OnStopSaveRequested;
        _hud.CancelRequested += OnCancelRequested;
        _hud.LockChanged += OnLockChanged;
        _hud.DrawToggled += OnDrawToggled;
        _hud.DrawColorChanged += OnDrawColorChanged;
        _hud.MicMuteToggled += OnMicMuteToggled;

        _micMuted = settings.MicrophoneEnabled && settings.MicrophoneMuted;
        _hud.InitMic(settings.MicrophoneEnabled, _micMuted);

        int virtualScreenH = Services.ScreenFreezeService.GetVirtualScreenBounds().Height;
        _hud.PositionBelowRegion(x, y, w, h, virtualScreenH);
        _hud.Activate();
        _hud.Start();

        // Exclude the HUD + region border from capture (WDA_EXCLUDEFROMCAPTURE,
        // works for WGC and gdigrab); the draw overlay stays visible.
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

        if (isFfmpegCodec)
        {
            // VP9 / AV1 — record natively via FFmpeg (gdigrab + wasapi loopback)
            if (!FFmpegService.Instance.IsAvailable)
            {
                NotificationService.Warning("WarnNoFfmpeg");
                Cleanup(discardTemp: true);
                return;
            }

            _ffmpegRec = new FFmpegRecordingService();
            _ffmpegRec.RecordingComplete += OnRecordingComplete;
            _ffmpegRec.RecordingFailed   += OnFfmpegRecordingFailed;
            try
            {
                _ffmpegRec.Start(x, y, w, h);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Clipsy] FFmpeg recording start failed: {ex.Message}");
                NotificationService.Error("ErrRecordFailed");
                Cleanup(discardTemp: true);
            }
            return;
        }

        // H.264 / H.265 — record via ScreenRecorderLib. Overlay exclusion was
        // already applied above (shared by both backends).
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

    private void OnPauseRequested()  { _service?.Pause();  _ffmpegRec?.Pause();  }
    private void OnResumeRequested() { _service?.Resume(); _ffmpegRec?.Resume(); }

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
        DestroyVisualOverlays();
        try { _service?.Stop(); Diagnostics.Log("  _service.Stop OK"); }
        catch (Exception ex) { Diagnostics.Log("OnStopRequested _service.Stop", ex); }
        try { _ffmpegRec?.Stop(); Diagnostics.Log("  _ffmpegRec.Stop OK"); }
        catch (Exception ex) { Diagnostics.Log("OnStopRequested _ffmpegRec.Stop", ex); }
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
        DestroyVisualOverlays();
        try { _service?.Stop(); Diagnostics.Log("  _service.Stop OK"); }
        catch (Exception ex) { Diagnostics.Log("OnStopSaveRequested _service.Stop", ex); }
        try { _ffmpegRec?.Stop(); Diagnostics.Log("  _ffmpegRec.Stop OK"); }
        catch (Exception ex) { Diagnostics.Log("OnStopSaveRequested _ffmpegRec.Stop", ex); }
    }

    /// <summary>Cancel button: stop and discard temp file (no save, no dialog).</summary>
    private void OnCancelRequested()
    {
        Diagnostics.Log("RecordingController.OnCancelRequested ENTER");
        if (_stopping) { Diagnostics.Log("  already _stopping, skip"); return; }
        _stopping = true;
        _stopAndSave = false;     // OnRecordingComplete discards temp when false
        _saveAsDialog = false;
        _hudHwnd = _hud?.Hwnd ?? IntPtr.Zero;
        try { _hud?.Shutdown(); Diagnostics.Log("  _hud.Shutdown OK"); }
        catch (Exception ex) { Diagnostics.Log("OnCancelRequested _hud.Shutdown", ex); }
        DestroyVisualOverlays();
        try { _service?.Stop(); Diagnostics.Log("  _service.Stop OK"); }
        catch (Exception ex) { Diagnostics.Log("OnCancelRequested _service.Stop", ex); }
        try { _ffmpegRec?.Stop(); Diagnostics.Log("  _ffmpegRec.Stop OK"); }
        catch (Exception ex) { Diagnostics.Log("OnCancelRequested _ffmpegRec.Stop", ex); }
    }

    private void OnMicMuteToggled(bool muted)
    {
        _micMuted = muted;
        _service?.SetMicMuted(muted);
        var s = SettingsService.Instance.Settings;
        s.MicrophoneMuted = muted;
        SettingsService.Instance.Save();
    }

    public void ToggleMic()
    {
        _micMuted = !_micMuted;
        _service?.SetMicMuted(_micMuted);
        _hud?.SetMicMuted(_micMuted);
    }

    private void OnDrawColorChanged(byte r, byte g, byte b)
    {
        _drawColor = (r, g, b);
        _drawWin?.SetColor(r, g, b);
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
            _ffmpegRec?.UpdateRegion(_x, _y, _w, _h);
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
                    _drawWin.SetColor(_drawColor.R, _drawColor.G, _drawColor.B);
                    _drawWin.SetThickness(3);
                    try { Recorder.SetExcludeFromCapture(_drawWin.Hwnd, false); } catch { }
                }
                _drawWin.SetActive(true);

                // Overlay swallows clicks via a 1/255 alpha bg; when the HUD tucks
                // inside the region, cut a hole over it so its toolbar stays clickable.
                if (_hud != null && GetWindowRect(_hud.Hwnd, out RECT hud))
                    _drawWin.SetExcludeRect(hud.left, hud.top, hud.right - hud.left, hud.bottom - hud.top);
            }
            else
            {
                // Keep existing strokes; clear is explicit, not auto on toggle
                // (RMB-drag erases; full clear needs a dedicated UI hook — TODO).
                _drawWin?.SetActive(false);
                _drawWin?.ClearExcludeRect();
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
                        await SilentSaveAsync(filePath);
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

    private async Task SilentSaveAsync(string tempPath)
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
            var fmt = _outputFmt;
            var name = SaveDialogService.MakeTimestampName("Clipsy", fmt);
            var dest = Path.Combine(folder, name);
            Diagnostics.Log($"  dest='{dest}' native='{_nativeFmt}' target='{fmt}'");
            // Container swap or GIF conversion when the chosen format differs from
            // the encoder's; may redirect to MP4 if AVI/MKV needs missing FFmpeg.
            var actual = await ConvertOrCopyAsync(tempPath, dest, _nativeFmt, fmt);
            Diagnostics.Log("  ConvertOrCopyAsync OK");
            TryDelete(tempPath);
            settings.Settings.LastVideoFolder = folder;
            settings.Save();
            NotifyVideoSaved(actual, dest, fmt);
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
            // H.265 → H.264 automatic fallback (hardware may not support H.265)
            if (!_h265FallbackAttempted &&
                SettingsService.Instance.Settings.VideoCodec == "H.265" &&
                _service != null)
            {
                _h265FallbackAttempted = true;
                NotificationService.Warning("WarnCodecFallback");
                // Output format stays mp4 (ScreenRecorderLib always records mp4)
                try { _service.Dispose(); } catch { }
                _service = new RecordingService();
                _service.RecordingComplete += OnRecordingComplete;
                _service.RecordingFailed   += OnRecordingFailed;
                try
                {
                    _service.Start(_x, _y, _w, _h, overrideCodec: "H.264");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[Clipsy] H.264 fallback start failed: {ex.Message}");
                    NotificationService.Error("ErrRecordFailed");
                    try { Cleanup(discardTemp: true); }
                    catch (Exception cex) { Diagnostics.Log("OnRecordingFailed fallback Cleanup", cex); }
                }
                return;
            }

            NotificationService.Error("ErrRecordRuntime");
            try { Cleanup(discardTemp: true); }
            catch (Exception ex) { Diagnostics.Log("OnRecordingFailed Cleanup", ex); }
        });
    }

    private void OnFfmpegRecordingFailed(string error)
    {
        Diagnostics.Log($"RecordingController.OnFfmpegRecordingFailed: {error}");
        _ui.TryEnqueue(() =>
        {
            NotificationService.Error("ErrRecordRuntime");
            try { Cleanup(discardTemp: true); }
            catch (Exception ex) { Diagnostics.Log("OnFfmpegRecordingFailed Cleanup", ex); }
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
        var preferredFmt = _outputFmt;
        var name = SaveDialogService.MakeTimestampName("Clipsy", preferredFmt);
        // Prefer HostWindow over HUD hwnd: HUD is a TOOLWINDOW + NOACTIVATE +
        // TRANSPARENT click-through window — invalid modal owner for common dialogs.
        var hwnd = App.Current?.HostWindow?.Hwnd ?? _hudHwnd;

        // MP4/GIF work without FFmpeg; AVI/MKV need it for remux, so offer them
        // only when present. Preferred format floats to the top.
        bool ffmpeg = FFmpegService.Instance.IsAvailable;
        var filters = new System.Collections.Generic.List<SaveDialogService.SaveFilter>
        {
            new("MP4 video (*.mp4)",    "*.mp4"),
        };
        if (ffmpeg)
        {
            filters.Add(new("MKV video (*.mkv)", "*.mkv"));
            filters.Add(new("AVI video (*.avi)", "*.avi"));
        }
        filters.Add(new("GIF animation (*.gif)", "*.gif"));

        int preferredIdx = filters.FindIndex(f =>
            SaveDialogService.ExtensionFromPattern(f.Pattern).TrimStart('.')
                .Equals(preferredFmt, StringComparison.OrdinalIgnoreCase));
        if (preferredIdx < 0) preferredIdx = 0;
        if (preferredIdx > 0)
        {
            var picked = filters[preferredIdx];
            filters.RemoveAt(preferredIdx);
            filters.Insert(0, picked);
        }

        Diagnostics.Log($"  hwnd=0x{hwnd.ToInt64():X}, initialDir='{initialDir}', suggested='{name}', preferred='{preferredFmt}'");
        SaveDialogService.SavePickResult? pick = null;
        try
        {
            pick = await SaveDialogService.PickSaveAsync(hwnd, initialDir!, name, filters, "." + preferredFmt);
            Diagnostics.Log($"  PickSaveAsync returned pick={(pick == null ? "null" : $"'{pick.Path}' filter={pick.FilterIndex}")}");
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

        // Figure out chosen extension from filter index (fall back to file ext).
        var chosenFilter = filters[System.Math.Max(0, pick.FilterIndex - 1)];
        var chosenExt = SaveDialogService.ExtensionFromPattern(chosenFilter.Pattern); // ".mp4"/...
        var chosenFmt = chosenExt.TrimStart('.').ToLowerInvariant();
        var dest = pick.Path;
        if (!dest.EndsWith(chosenExt, StringComparison.OrdinalIgnoreCase))
            dest = Path.ChangeExtension(dest, chosenExt);

        try
        {
            var actual = await ConvertOrCopyAsync(tempPath, dest, _nativeFmt, chosenFmt);
            TryDelete(tempPath);
            var dir = Path.GetDirectoryName(actual);
            if (!string.IsNullOrEmpty(dir))
            {
                settings.Settings.LastVideoFolder = dir;
                settings.Save();
            }
            NotifyVideoSaved(actual, dest, chosenFmt);
            Diagnostics.Log($"  AfterSaveAction.Run action='{settings.Settings.AfterSaveAction}'");
            AfterSaveAction.Run(dest, settings.Settings.AfterSaveAction);
            Diagnostics.Log("OfferSaveAsync EXIT OK");
        }
        catch (Exception ex)
        {
            Diagnostics.Log("OfferSaveAsync save", ex);
            NotificationService.Error("ErrSaveFailed");
        }
    }

    /// <summary>Move the temp recording to dest, converting container/GIF if the
    /// chosen format differs; returns the path actually written (may be MP4).</summary>
    /// <summary>Post the "saved" toast, noting the MP4 fallback when AVI/MKV was
    /// redirected because FFmpeg is missing.</summary>
    private static void NotifyVideoSaved(string actualPath, string requestedPath, string requestedFmt)
    {
        long sizeKb = new FileInfo(actualPath).Length / 1024L;
        var fileName = Path.GetFileName(actualPath);

        if (!string.Equals(actualPath, requestedPath, StringComparison.OrdinalIgnoreCase))
            NotificationService.VideoSavedAsMp4(fileName, sizeKb, actualPath, requestedFmt);
        else
            NotificationService.VideoSaved(fileName, sizeKb, actualPath);
    }

    private static async Task<string> ConvertOrCopyAsync(string src, string dest, string srcFmt, string destFmt)
    {
        if (string.Equals(srcFmt, destFmt, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(src, dest, overwrite: true);
            return dest;
        }
        if (destFmt == "gif")
        {
            // FFmpeg first; fall back to NativeGifEncoder, error only if both fail.
            bool ok = false;
            if (FFmpegService.Instance.IsAvailable)
            {
                ok = await FFmpegService.Instance.ConvertToGifAsync(src, dest);
                if (!GifOutputOk(dest, ok))
                {
                    Diagnostics.Log("ConvertOrCopyAsync gif via FFmpeg failed → NativeGifEncoder fallback");
                    ok = false;
                }
            }

            if (!GifOutputOk(dest, ok))
            {
                Diagnostics.Log("ConvertOrCopyAsync gif via NativeGifEncoder");
                ok = await NativeGifEncoder.ConvertMp4ToGifAsync(src, dest);
            }

            if (!GifOutputOk(dest, ok))
            {
                Diagnostics.Log("ConvertOrCopyAsync gif conversion failed (ffmpeg + native)");
                throw new InvalidOperationException("GIF conversion failed.");
            }
            return dest;
        }

        // Container swap (mp4 ↔ avi/mkv). FFmpeg does a clean stream copy.
        if (FFmpegService.Instance.IsAvailable)
        {
            var args = $"-i \"{src}\" -c copy -y \"{dest}\"";
            var ok = await FFmpegService.Instance.RunAsync(args);
            if (ok && File.Exists(dest)) return dest;
            Diagnostics.Log($"ConvertOrCopyAsync ffmpeg remux failed src='{src}' dest='{dest}'");
        }

        // No FFmpeg: a plain rename would put MP4 bytes in an AVI/MKV container,
        // so keep the native MP4 and let the caller surface the notice.
        if (destFmt is "avi" or "mkv")
        {
            var mp4Dest = Path.ChangeExtension(dest, ".mp4");
            File.Copy(src, mp4Dest, overwrite: true);
            Diagnostics.Log($"ConvertOrCopyAsync {destFmt} requested but FFmpeg missing → saved '{mp4Dest}'");
            return mp4Dest;
        }

        File.Copy(src, dest, overwrite: true);
        return dest;
    }

    private static bool GifOutputOk(string dest, bool ok)
        => ok && File.Exists(dest) && new FileInfo(dest).Length > 0;

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
            try { _ffmpegRec?.Dispose(); Diagnostics.Log("  ffmpegRec.Dispose OK"); } catch (Exception ex) { Diagnostics.Log("Cleanup ffmpegRec.Dispose", ex); }
            if (discardTemp)
            {
                var tempPath = _service?.TempPath ?? _ffmpegRec?.TempPath;
                if (!string.IsNullOrEmpty(tempPath)) TryDelete(tempPath);
            }
        }
        finally
        {
            _border = null;
            _hud = null;
            _drawWin = null;
            _resizeWin = null;
            _service = null;
            _ffmpegRec = null;
            _h265FallbackAttempted = false;
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
