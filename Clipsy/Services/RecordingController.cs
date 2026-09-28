using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Clipsy.Views.Recording;
using Microsoft.UI.Dispatching;
using ScreenRecorderLib;

namespace Clipsy.Services;

/// <summary>Owns the recording session (region border + HUD, RecordingService, stop/save).
/// Singleton: one recording (including its finalizing/saving phase) at a time.</summary>
public sealed class RecordingController
{
    private enum StopMode { Save, SaveAs, Discard }

    private static RecordingController? _current;
    public static RecordingController? Current => _current;

    /// <summary>Capturing right now; hotkeys stop the recording instead of opening the overlay.</summary>
    public static bool IsRecording => _current is { _stopping: false };

    /// <summary>A session exists, possibly still finalizing/saving the previous recording.</summary>
    public static bool IsBusy => _current != null;

    // Codec fallback is only safe before anything worth keeping was recorded.
    private const long FallbackWindowMs = 3000;

    private readonly DispatcherQueue _ui;
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Win32BorderOverlay? _border;
    private RecordingHudWindow? _hud;
    private Win32DrawingOverlay? _drawWin;
    private Win32ResizeOverlay? _resizeWin;
    private RecordingService? _service;
    private bool _h265FallbackAttempted;
    private long _startedTick;
    private string _outputFmt = "mp4";   // user-chosen container for the final file
    private string? _transcodeCodec;     // VP9/AV1: recorded as H.264, re-encoded on save
    private int _x, _y, _w, _h;
    private StopMode _stopMode;
    private bool _stopping;
    private bool _micMuted;
    private IntPtr _hudHwnd;
    private (byte R, byte G, byte B) _drawColor = (0xFF, 0x00, 0x00);

    private RecordingController(DispatcherQueue ui) { _ui = ui; }

    public static bool TryStart(int x, int y, int w, int h)
    {
        if (_current != null)
        {
            NotificationService.Info("RecordBusy");
            return false;
        }
        var ui = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Recording must be started from the UI thread.");
        var c = new RecordingController(ui);
        _current = c;
        try
        {
            c.Start(x, y, w, h);
            return ReferenceEquals(_current, c);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("RecordingController.TryStart", ex);
            try { c.Cleanup(discardTemp: true); }
            catch (Exception cleanupEx) { Diagnostics.Log("RecordingController.TryStart Cleanup", cleanupEx); }
            NotificationService.Error("ErrRecordFailed");
            return false;
        }
    }

    public void StopFromHotkey() => Stop(StopMode.Save);

    /// <summary>Stops and saves (if still recording) and waits for the file to be written.</summary>
    public async Task StopForExitAsync(TimeSpan timeout)
    {
        if (!_stopping) Stop(StopMode.Save);
        await Task.WhenAny(_finished.Task, Task.Delay(timeout));
    }

    private void Start(int x, int y, int w, int h)
    {
        _x = x; _y = y; _w = w; _h = h;

        var settingsService = SettingsService.Instance;
        var settings = settingsService.Settings;
        if (!settings.MicrophoneStateInitialized)
        {
            settings.MicrophoneMuted = true;
            settings.MicrophoneStateInitialized = true;
            settingsService.SaveState();
        }

        // ScreenRecorderLib always writes H.264/H.265 MP4; VP9/AV1 are produced from it on save.
        bool wantsTranscode = settings.VideoCodec is "VP9" or "AV1";
        if (wantsTranscode && !FFmpegService.Instance.IsAvailable)
        {
            NotificationService.Warning("WarnNoFfmpeg");
            wantsTranscode = false;
        }
        _transcodeCodec = wantsTranscode ? settings.VideoCodec : null;
        _outputFmt = wantsTranscode ? "mkv" : settings.VideoFormat ?? "mp4";

        _border = new Win32BorderOverlay();
        _border.Create(x, y, w, h);

        _hud = new RecordingHudWindow();
        _hud.PauseRequested += () => _service?.Pause();
        _hud.ResumeRequested += () => _service?.Resume();
        _hud.StopRequested += () => Stop(StopMode.Save);
        _hud.StopSaveRequested += () => Stop(StopMode.SaveAs);
        _hud.CancelRequested += () => Stop(StopMode.Discard);
        _hud.LockChanged += OnLockChanged;
        _hud.DrawToggled += OnDrawToggled;
        _hud.DrawColorChanged += OnDrawColorChanged;
        _hud.MicMuteToggled += OnMicMuteToggled;

        _micMuted = settings.MicrophoneEnabled && settings.MicrophoneMuted;
        _hud.InitMic(settings.MicrophoneEnabled, _micMuted);
        _hud.PositionBelowRegion(x, y, w, h);
        _hud.Activate();
        _hud.Start();

        // Exclude the HUD + region border from capture (WDA_EXCLUDEFROMCAPTURE); the draw overlay stays visible.
        try
        {
            Recorder.SetExcludeFromCapture(_hud.Hwnd, true);
            if (_border.Hwnd != IntPtr.Zero)
                Recorder.SetExcludeFromCapture(_border.Hwnd, true);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("SetExcludeFromCapture failed", ex);
        }

        StartService(overrideCodec: null);
    }

    private void StartService(string? overrideCodec)
    {
        _service = new RecordingService();
        _service.RecordingComplete += OnRecordingComplete;
        _service.RecordingFailed += OnRecordingFailed;
        _startedTick = Environment.TickCount64;
        _service.Start(_x, _y, _w, _h, overrideCodec);
    }

    private void Stop(StopMode mode)
    {
        if (_stopping) return;
        Diagnostics.Log($"Recording stop: {mode}");
        _stopping = true;
        _stopMode = mode;
        _hudHwnd = _hud?.Hwnd ?? IntPtr.Zero;
        try { _hud?.Shutdown(); } catch (Exception ex) { Diagnostics.Log("Recording stop: HUD shutdown", ex); }
        DestroyVisualOverlays();
        try
        {
            if (_service != null) _service.Stop();
            else Cleanup(discardTemp: true);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Recording stop: service", ex);
            Cleanup(discardTemp: mode == StopMode.Discard);
            return;
        }
        _ = WatchStopAsync();
    }

    // The recorder normally reports completion within seconds; never stay "busy" forever.
    // The temp file is kept, so the next start recovers it if it's playable.
    private async Task WatchStopAsync()
    {
        if (await Task.WhenAny(_finished.Task, Task.Delay(TimeSpan.FromSeconds(60))) == _finished.Task) return;
        Diagnostics.Log("Recorder did not report completion after Stop; releasing the session.");
        _ui.TryEnqueue(() => Cleanup(discardTemp: false));
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

    private void OnMicMuteToggled(bool muted)
    {
        _micMuted = muted;
        _service?.SetMicMuted(muted);
        PersistMicState();
    }

    public void ToggleMic()
    {
        if (!SettingsService.Instance.Settings.MicrophoneEnabled || _stopping) return;
        _micMuted = !_micMuted;
        _service?.SetMicMuted(_micMuted);
        _hud?.SetMicMuted(_micMuted);
        PersistMicState();
    }

    private void PersistMicState()
    {
        var settings = SettingsService.Instance;
        settings.Settings.MicrophoneMuted = _micMuted;
        settings.Settings.MicrophoneStateInitialized = true;
        settings.SaveState();
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
                    _resizeWin.RegionChanged += ApplyRegionChange;
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
            Diagnostics.Log("Region lock toggle failed", ex);
        }
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
            _hud?.PositionBelowRegion(_x, _y, _w, _h);
            _drawWin?.MoveTo(_x, _y, _w, _h);
            _resizeWin?.MoveTo(_x, _y, _w, _h);
            _service?.UpdateRegion(_x, _y, _w, _h);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Region update failed", ex);
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
                // Strokes stay when drawing is toggled off; RMB-drag erases them.
                _drawWin?.SetActive(false);
                _drawWin?.ClearExcludeRect();
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Draw overlay toggle failed", ex);
        }
    }

    private void OnRecordingComplete(string filePath)
    {
        Diagnostics.Log($"Recording complete: '{filePath}' exists={File.Exists(filePath)} mode={_stopMode} stopping={_stopping}");
        _ui.TryEnqueue(async () =>
        {
            try
            {
                if (!_stopping)
                    Diagnostics.Log("Recorder finished on its own; saving what was recorded.");
                switch (_stopping ? _stopMode : StopMode.Save)
                {
                    case StopMode.SaveAs: await OfferSaveAsync(filePath); break;
                    case StopMode.Save: await SilentSaveAsync(filePath); break;
                    default: TryDelete(filePath); break;
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Recording save", ex);
                await RescueAsync(filePath);
            }
            finally
            {
                try { Cleanup(discardTemp: false); }
                catch (Exception ex) { Diagnostics.Log("Recording Cleanup", ex); }
            }
        });
    }

    private void OnRecordingFailed(string error)
    {
        Diagnostics.Log($"Recording failed: {error}");
        _ui.TryEnqueue(() =>
        {
            // H.265 → H.264 fallback, only right after start: later it would silently drop what was
            // recorded, and after Stop it would start a hidden, unstoppable recording.
            bool early = Environment.TickCount64 - _startedTick < FallbackWindowMs;
            if (!_h265FallbackAttempted && !_stopping && early &&
                SettingsService.Instance.Settings.VideoCodec == "H.265" && _service != null)
            {
                _h265FallbackAttempted = true;
                NotificationService.Warning("WarnCodecFallback");
                var failedTemp = _service.TempPath;
                try { _service.Dispose(); } catch { }
                TryDelete(failedTemp);
                try
                {
                    StartService(overrideCodec: "H.264");
                    _hud?.RestartTimer();
                    return;
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("H.264 fallback start failed", ex);
                }
            }

            NotificationService.Error("ErrRecordRuntime");
            try { Cleanup(discardTemp: true); }
            catch (Exception ex) { Diagnostics.Log("OnRecordingFailed Cleanup", ex); }
        });
    }

    private async Task SilentSaveAsync(string tempPath)
    {
        var settings = SettingsService.Instance;
        var folder = settings.GetEffectiveVideoFolder();
        var dest = SaveDialogService.UniquePath(folder, "Clipsy", _outputFmt);
        var actual = await ConvertOrMoveAsync(tempPath, dest, _outputFmt);
        settings.Settings.LastVideoFolder = folder;
        settings.SaveState();
        NotifyVideoSaved(actual, dest, _outputFmt);
        AfterSaveAction.Run(actual, settings.Settings.AfterSaveAction);
    }

    private async Task OfferSaveAsync(string tempPath)
    {
        var settings = SettingsService.Instance;
        var initialDir = settings.GetEffectiveVideoFolder();
        var preferredFmt = _outputFmt;
        var name = SaveDialogService.MakeTimestampName("Clipsy", preferredFmt);
        // Prefer HostWindow over HUD hwnd: HUD is a TOOLWINDOW + NOACTIVATE +
        // click-through window — an invalid modal owner for common dialogs.
        var hwnd = App.Current?.HostWindow?.Hwnd ?? _hudHwnd;

        // MP4/GIF work without FFmpeg; AVI/MKV need it, so offer them only when present.
        bool ffmpeg = FFmpegService.Instance.IsAvailable;
        var filters = new System.Collections.Generic.List<SaveDialogService.SaveFilter> { new("MP4 video (*.mp4)", "*.mp4") };
        if (ffmpeg)
        {
            filters.Add(new("MKV video (*.mkv)", "*.mkv"));
            filters.Add(new("AVI video (*.avi)", "*.avi"));
        }
        filters.Add(new("GIF animation (*.gif)", "*.gif"));
        int preferredIdx = filters.FindIndex(f =>
            SaveDialogService.ExtensionFromPattern(f.Pattern).Equals(preferredFmt, StringComparison.OrdinalIgnoreCase));
        if (preferredIdx > 0)
        {
            var picked = filters[preferredIdx];
            filters.RemoveAt(preferredIdx);
            filters.Insert(0, picked);
        }

        SaveDialogService.SavePickResult? pick;
        try
        {
            pick = await SaveDialogService.PickSaveAsync(hwnd, initialDir, name, filters, "." + filters[0].Pattern[2..]);
        }
        catch (Exception ex)
        {
            // The dialog failing is not the user cancelling: keep the recording.
            Diagnostics.Log("Save As dialog failed; saving to the default folder", ex);
            await SilentSaveAsync(tempPath);
            return;
        }
        if (pick == null)
        {
            TryDelete(tempPath); // user cancelled Save As = discard
            return;
        }

        var chosenFilter = filters[Math.Max(0, pick.FilterIndex - 1)];
        var chosenFmt = SaveDialogService.ExtensionFromPattern(chosenFilter.Pattern);
        var dest = pick.Path;
        if (!dest.EndsWith("." + chosenFmt, StringComparison.OrdinalIgnoreCase))
            dest = Path.ChangeExtension(dest, "." + chosenFmt);

        var actual = await ConvertOrMoveAsync(tempPath, dest, chosenFmt);
        var dir = Path.GetDirectoryName(actual);
        if (!string.IsNullOrEmpty(dir))
        {
            settings.Settings.LastVideoFolder = dir;
            settings.SaveState();
        }
        NotifyVideoSaved(actual, dest, chosenFmt);
        AfterSaveAction.Run(actual, settings.Settings.AfterSaveAction);
    }

    /// <summary>Produces <paramref name="dest"/> from the temp MP4 and removes the temp file.
    /// Returns the path actually written (an .mp4 next to dest when conversion isn't possible).</summary>
    private async Task<string> ConvertOrMoveAsync(string src, string dest, string destFmt)
    {
        var ffmpeg = FFmpegService.Instance;
        if (destFmt == "gif")
        {
            NotificationService.Info("VideoConverting");
            bool ok = ffmpeg.IsAvailable && await ffmpeg.ConvertToGifAsync(src, dest) && NonEmpty(dest);
            if (!ok)
            {
                var (nativeOk, truncated) = await NativeGifEncoder.ConvertMp4ToGifAsync(src, dest);
                ok = nativeOk && NonEmpty(dest);
                if (ok && truncated) NotificationService.Warning("WarnGifTruncated");
            }
            if (!ok) throw new InvalidOperationException("GIF conversion failed.");
            TryDelete(src);
            return dest;
        }

        if (destFmt == "mp4")
        {
            await MoveAsync(src, dest);
            return dest;
        }

        bool converted = false;
        if (ffmpeg.IsAvailable)
        {
            if (_transcodeCodec != null && destFmt == "mkv")
            {
                NotificationService.Info("VideoConverting");
                converted = await ffmpeg.TranscodeAsync(src, dest, _transcodeCodec, SettingsService.Instance.Settings.VideoBitrateMbps) && NonEmpty(dest);
                if (!converted) Diagnostics.Log($"{_transcodeCodec} transcode failed; remuxing H.264 instead");
            }
            if (!converted)
                converted = await ffmpeg.RemuxAsync(src, dest) && NonEmpty(dest);
        }
        if (converted)
        {
            TryDelete(src);
            return dest;
        }

        // Keep the MP4 rather than putting MP4 bytes behind an .avi/.mkv name.
        TryDelete(dest);
        var mp4 = SaveDialogService.UniquePath(Path.GetDirectoryName(dest)!, Path.GetFileNameWithoutExtension(dest), "mp4", timestamp: false);
        await MoveAsync(src, mp4);
        return mp4;
    }

    private static Task MoveAsync(string src, string dest)
        => Task.Run(() => File.Move(src, dest, overwrite: true)); // copies + deletes across volumes, off the UI thread

    /// <summary>Last resort after a failed save: move the recording where the user can find it.</summary>
    private static async Task RescueAsync(string tempPath)
    {
        if (!File.Exists(tempPath))
        {
            NotificationService.Error("ErrSaveFailed");
            return;
        }
        try
        {
            var folder = SettingsService.Instance.GetEffectiveVideoFolder();
            var dest = SaveDialogService.UniquePath(folder, "Clipsy", "mp4");
            await MoveAsync(tempPath, dest);
            NotificationService.VideoKeptAfterFailure(dest);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("Recording rescue failed", ex);
            NotificationService.VideoKeptAfterFailure(tempPath);
        }
    }

    private static bool NonEmpty(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    /// <summary>Post the "saved" toast, noting the MP4 fallback when conversion wasn't possible.</summary>
    private static void NotifyVideoSaved(string actualPath, string requestedPath, string requestedFmt)
    {
        long sizeKb = new FileInfo(actualPath).Length / 1024L;
        var fileName = Path.GetFileName(actualPath);
        if (!string.Equals(actualPath, requestedPath, StringComparison.OrdinalIgnoreCase))
            NotificationService.VideoSavedAsMp4(fileName, sizeKb, actualPath, requestedFmt, ffmpegMissing: !FFmpegService.Instance.IsAvailable);
        else
            NotificationService.VideoSaved(fileName, sizeKb, actualPath);
    }

    private void Cleanup(bool discardTemp)
    {
        try
        {
            try { _hud?.Shutdown(); } catch (Exception ex) { Diagnostics.Log("Cleanup hud.Shutdown", ex); }
            try { _hud?.Close(); } catch (Exception ex) { Diagnostics.Log("Cleanup hud.Close", ex); }
            DestroyVisualOverlays();
            var tempPath = _service?.TempPath;
            try { _service?.Dispose(); } catch (Exception ex) { Diagnostics.Log("Cleanup service.Dispose", ex); }
            if (discardTemp && !string.IsNullOrEmpty(tempPath)) TryDelete(tempPath);
        }
        finally
        {
            _hud = null;
            _service = null;
            _stopping = true;
            if (ReferenceEquals(_current, this)) _current = null;
            _finished.TrySetResult();
        }
    }

    /// <summary>Recordings left in %TEMP% by a crash or kill: playable ones (MP4 with an index) are moved
    /// to the video folder, broken week-old ones are removed.</summary>
    public static void RecoverOrphanedRecordings()
    {
        Task.Run(() =>
        {
            try
            {
                var dir = RecordingService.TempDirectory;
                if (!Directory.Exists(dir)) return;
                int recovered = 0;
                string? lastPath = null;
                foreach (var file in Directory.GetFiles(dir, "recording_*.mp4"))
                {
                    var info = new FileInfo(file);
                    if (info.Length == 0) { TryDelete(file); continue; }
                    if (Mp4HasIndex(file))
                    {
                        var dest = SaveDialogService.UniquePath(SettingsService.Instance.GetEffectiveVideoFolder(), "Clipsy_recovered", "mp4");
                        File.Move(file, dest);
                        recovered++;
                        lastPath = dest;
                    }
                    else if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromDays(7))
                    {
                        TryDelete(file);
                    }
                }
                foreach (var file in Directory.GetFiles(dir, "palette_*.png")) TryDelete(file);
                if (recovered > 0 && lastPath != null)
                {
                    Diagnostics.Log($"Recovered {recovered} orphaned recording(s)");
                    App.Current?.HostWindow?.DispatcherQueue.TryEnqueue(() => NotificationService.VideoRecovered(lastPath));
                }
            }
            catch (Exception ex) { Diagnostics.Log("Orphaned recording recovery failed", ex); }
        });
    }

    // An interrupted MP4 has no 'moov' box and can't be played; scan top-level boxes for it.
    internal static bool Mp4HasIndex(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var header = new byte[16];
            long pos = 0;
            while (pos + 8 <= fs.Length)
            {
                fs.Position = pos;
                if (fs.Read(header, 0, 8) < 8) return false;
                long size = (uint)(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]);
                string type = System.Text.Encoding.ASCII.GetString(header, 4, 4);
                if (size == 1)
                {
                    if (fs.Read(header, 8, 8) < 8) return false;
                    size = (long)((ulong)header[8] << 56 | (ulong)header[9] << 48 | (ulong)header[10] << 40 | (ulong)header[11] << 32
                         | (ulong)header[12] << 24 | (ulong)header[13] << 16 | (ulong)header[14] << 8 | header[15]);
                }
                if (type == "moov") return true;
                if (size < 8) return false; // size 0 = "to end of file" (unfinished mdat)
                pos += size;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) { Diagnostics.Log($"Delete '{path}' failed: {ex.Message}"); }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
