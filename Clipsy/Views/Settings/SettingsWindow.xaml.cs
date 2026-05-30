using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Clipsy.Localization;
using Clipsy.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace Clipsy.Views.Settings;

public sealed partial class SettingsWindow : Window
{
    private static SettingsWindow? _current;

    private readonly IntPtr _hwnd;
    private AppSettings _draft;
    private AppSettings _initial = new();
    private readonly ObservableCollection<HotkeyRow> _hotkeyRows = new();
    private readonly HashSet<string> _dirty = new();
    private Button? _listeningButton;
    private string? _listeningKey;

    private bool _firstActivated;
    // Starts true so RangeBase / ComboBox handlers that fire during
    // InitializeComponent (from XAML attribute setters like Value="8")
    // don't run MarkChanged() before the XAML tree is populated. Load()
    // flips this off once the initial draft has been applied.
    private bool _loading = true;

    // Tessdata language management
    private readonly HashSet<string> _tessSelectedCodes = new();
    private readonly Dictionary<string, CancellationTokenSource> _tessDownloadCts = new();

    // FFmpeg download
    private CancellationTokenSource? _ffmpegCts;

    // Autostart lives in registry (not AppSettings) — track init state separately
    private bool _initialAutostart;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _notifyTimer;

    // Sidebar tip rotation: a shuffled queue of tip keys cycled on a timer with
    // a fade swap. Not tied to the active tab — purely ambient.
    private static readonly string[] _tipKeys =
    {
        "TipPrtScCapture",
        "TipRotateDragRegion",
        "TipRotateErase",
        "TipRotateOcr",
        "TipRotateGif",
        "TipRotateHotkeys",
        "TipRotateLock",
    };
    private static readonly Random _tipRng = new();
    private readonly List<int> _tipOrder = new();
    private int _tipPos = -1;
    private int _currentTipKeyIndex;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tipTimer;

    private static readonly Dictionary<string, string> _paramToCategory = new()
    {
        ["lang"] = "general",
        ["theme"] = "general",
        ["ocr"] = "ocr",
        ["ss-folder"] = "general",
        ["vid-folder"] = "general",
        ["remember"] = "general",
        ["autostart"] = "general",
        ["ss-format"] = "general",
        ["jpg-q"] = "general",
        ["ss-cursor"] = "general",
        ["after-save"] = "general",
        ["update-int"] = "general",
        ["notif"] = "notifications",
        ["translate-svc"]  = "ocr",
        ["translate-from"] = "ocr",
        ["translate-to"]   = "ocr",
        ["codec"] = "video",
        ["resolution"] = "video",
        ["vid-cursor"] = "video",
        ["bitrate"] = "video",
        ["vid-format"] = "video",
        ["mic-enabled"] = "video",
        ["mic-device"] = "video",
        ["gif-color"] = "gif",
        ["gif-fps"] = "gif",
        ["gif-dither"] = "gif",
        // hk-* dynamic, mapped to hotkeys category
    };

    public SettingsWindow()
    {
        InitializeComponent();
        ThemeService.Register(Content as FrameworkElement);
        _hwnd = WindowNative.GetWindowHandle(this);
        _draft = SettingsService.Instance.Settings.Clone();
        HotkeyList.ItemsSource = _hotkeyRows;

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("SettingsWindow.SetTitleBar", ex);
        }

        // Cloak the window via DWM until XAML has composed its first frame.
        // Without this DWM briefly shows the default opaque window surface
        // (visible as a black flash) before the dark theme paints.
        int cloak = 1;
        try { DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloak, sizeof(int)); }
        catch { }

        Activated += OnFirstActivated;
        if (Content is FrameworkElement fe)
        {
            fe.Loaded += (_, _) =>
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Uncloak);
        }
        Closed += (_, _) => { _tipTimer?.Stop(); if (_current == this) _current = null; };
    }

    private bool _cloaked = true;
    private const int DWMWA_CLOAK = 13;

    private void Uncloak()
    {
        if (!_cloaked) return;
        _cloaked = false;
        int cloak = 0;
        try { DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref cloak, sizeof(int)); }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);


    private void OnFirstActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_firstActivated) return;
        _firstActivated = true;
        try
        {
            var appWin = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
            appWin.Title = Strings.Get("TraySettings");
            appWin.Resize(new SizeInt32(940, 640));
            try
            {
                var tb = appWin.TitleBar;
                var transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);
                var fg = Windows.UI.Color.FromArgb(0xFF, 0xB5, 0xBA, 0xC1);
                var fgHover = Windows.UI.Color.FromArgb(0xFF, 0xF2, 0xF3, 0xF5);
                var hover = Windows.UI.Color.FromArgb(0xFF, 0x35, 0x37, 0x3C);
                var pressed = Windows.UI.Color.FromArgb(0xFF, 0x40, 0x42, 0x49);
                tb.ButtonBackgroundColor = transparent;
                tb.ButtonInactiveBackgroundColor = transparent;
                tb.ButtonForegroundColor = fg;
                tb.ButtonInactiveForegroundColor = Windows.UI.Color.FromArgb(0xFF, 0x80, 0x84, 0x8E);
                tb.ButtonHoverBackgroundColor = hover;
                tb.ButtonHoverForegroundColor = fgHover;
                tb.ButtonPressedBackgroundColor = pressed;
                tb.ButtonPressedForegroundColor = fgHover;
            }
            catch (Exception ex) { Diagnostics.Log("SettingsWindow.ThemeTitleBar", ex); }
            Load();
            ApplyLocalization();
            ThemeService.ApplyTo(Content as FrameworkElement);
            VersionLabel.Text = Strings.Get("VersionPrefix") + GetVersion();
            BuildDateLabel.Text = GetBuildDate();

            // Sync nav icon / pane visibility with default-checked radio
            foreach (var rb in new[] { NavGeneral, NavVideo, NavOcr, NavGif, NavHotkeys, NavNotifications, NavInfo })
            {
                if (rb.IsChecked == true) { OnNavChecked(rb, new RoutedEventArgs()); break; }
            }

            StartTipRotation();
        }
        catch (Exception ex)
        {
            Diagnostics.Show("SettingsWindow.OnFirstActivated", ex);
        }
    }

    public static void ShowOrActivate()
    {
        if (_current != null)
        {
            try { _current.Activate(); } catch (Exception ex) { Diagnostics.Show("SettingsWindow.Activate", ex); }
            return;
        }
        try
        {
            _current = new SettingsWindow();
            _current.Activate();
        }
        catch (Exception ex)
        {
            _current = null;
            Diagnostics.Show("SettingsWindow.Create", ex);
        }
    }

    private static string GetVersion()
    {
        return UpdateService.CurrentVersion();
    }

    private void Load()
    {
        _loading = true;
        SelectComboByTag(LangBox, _draft.Language);
        SelectSegment(_draft.Theme, ThemeBtnAuto, ThemeBtnDark, ThemeBtnLight);
        SelectComboByTag(OcrEngineBox, _draft.OcrEngine);
        _tessSelectedCodes.Clear();
        // Selection follows installation: every installed language is used
        // for OCR. Persisted TesseractLanguages is still merged in for
        // forward-compat with older configs.
        foreach (var c in _draft.TesseractLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            _tessSelectedCodes.Add(c);
        foreach (var lang in TessdataService.Catalog)
            if (TessdataService.IsInstalled(lang.Code))
                _tessSelectedCodes.Add(lang.Code);
        BuildTessLangRows();
        UpdateTessLangSectionVisibility();
        BuildTranslateLangDropdowns();
        SelectComboByTag(TranslateServiceBox, _draft.TranslateService);
        SelectComboByTag(TranslateFromBox,    _draft.TranslateFrom);
        SelectComboByTag(TranslateToBox,      _draft.TranslateTo);
        ScreenshotFolderBox.Text = string.IsNullOrEmpty(_draft.ScreenshotFolder)
            ? SettingsService.Instance.DefaultScreenshotFolder
            : _draft.ScreenshotFolder!;
        VideoFolderBox.Text = string.IsNullOrEmpty(_draft.VideoFolder)
            ? SettingsService.Instance.DefaultVideoFolder
            : _draft.VideoFolder!;
        // Mirror the displayed defaults back into the draft so the post-Load
        // _initial snapshot matches what Collect() will later read out of the
        // TextBoxes. Otherwise the first MarkChanged() after Load() compares
        // "default path" vs "" and falsely marks ss-folder/vid-folder dirty.
        _draft.ScreenshotFolder = ScreenshotFolderBox.Text;
        _draft.VideoFolder      = VideoFolderBox.Text;
        RememberFolderSwitch.IsChecked = _draft.RememberLastFolder;
        _initialAutostart = AutostartService.IsEnabled();
        AutostartSwitch.IsChecked = _initialAutostart;

        SelectComboByTag(ScreenshotFormatBox, _draft.ScreenshotFormat);
        ScreenshotCursorSwitch.IsChecked = _draft.CaptureScreenshotCursor;
        SelectComboByTag(VideoFormatBox, _draft.VideoFormat);
        VideoCursorSwitch.IsChecked = _draft.CaptureVideoCursor;

        JpgQualitySlider.Minimum = 50;
        JpgQualitySlider.Maximum = 100;
        JpgQualitySlider.Value = System.Math.Clamp(_draft.JpgQuality, 50, 100);
        JpgQualityLabel.Text = ((int)JpgQualitySlider.Value).ToString();
        UpdateJpgQualityRowVisibility();

        SelectComboByTag(AfterSaveBox, _draft.AfterSaveAction);
        SelectComboByTag(UpdateIntervalBox, _draft.UpdateInterval);

        NotifyMasterSwitch.IsChecked     = _draft.NotificationsEnabled;
        NotifyScreenshotSwitch.IsChecked = _draft.NotifyScreenshotSaved;
        NotifyVideoSwitch.IsChecked      = _draft.NotifyVideoSaved;
        NotifyClipboardSwitch.IsChecked  = _draft.NotifyClipboard;
        NotifyErrorsSwitch.IsChecked     = _draft.NotifyErrors;
        NotifyUpdateSwitch.IsChecked     = _draft.NotifyUpdateAvailable;
        NotifyHintsSwitch.IsChecked      = _draft.NotifyHints;
        UpdateNotifySubPanelState();

        SelectRadio(_draft.VideoCodec, RadioCodecH264, RadioCodecH265, RadioCodecVp9, RadioCodecAv1);
        SelectSegment(_draft.VideoResolution, ResBtn480p, ResBtn720p, ResBtn1080p, ResBtn1440p, ResBtnOriginal);
        UpdateBitrateBounds(_draft.VideoResolution);
        BitrateSlider.Value = System.Math.Clamp(_draft.VideoBitrateMbps, (int)BitrateSlider.Minimum, (int)BitrateSlider.Maximum);
        UpdateBitrateLabel();

        MicEnabledSwitch.IsChecked = _draft.MicrophoneEnabled;
        PopulateMicDevices(_draft.MicrophoneDevice);
        UpdateMicDevicePanelVisibility();

        GifColorSlider.Minimum = 16;
        GifColorSlider.Maximum = 256;
        GifColorSlider.Value = System.Math.Clamp(_draft.GifColors, 16, 256);
        GifColorLabel.Text = ((int)GifColorSlider.Value).ToString();

        GifFpsSlider.Minimum = 5;
        GifFpsSlider.Maximum = 30;
        GifFpsSlider.Value = System.Math.Clamp(_draft.GifFps, 5, 30);
        GifFpsLabel.Text = ((int)GifFpsSlider.Value).ToString();

        GifDitherSwitch.IsChecked = _draft.GifDither;

        BuildHotkeyRows();
        UpdateFfmpegSection();

        _initial = _draft.Clone();
        _dirty.Clear();
        _loading = false;
        UpdateDirtyVisuals();
    }

    private static void SelectComboByTag(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static string SelectedComboTag(ComboBox box)
    {
        return (box.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    }

    private void Collect()
    {
        _draft.Language = SelectedComboTag(LangBox);
        _draft.Theme = SelectedSegmentTag(ThemeBtnAuto, ThemeBtnDark, ThemeBtnLight);
        _draft.OcrEngine = SelectedComboTag(OcrEngineBox);
        _draft.TesseractLanguages = string.Join(",", _tessSelectedCodes);
        _draft.TranslateService = SelectedComboTag(TranslateServiceBox);
        _draft.TranslateFrom    = SelectedComboTag(TranslateFromBox);
        _draft.TranslateTo      = SelectedComboTag(TranslateToBox);
        _draft.ScreenshotFolder = ScreenshotFolderBox.Text;
        _draft.VideoFolder = VideoFolderBox.Text;
        _draft.RememberLastFolder = RememberFolderSwitch.IsChecked == true;
        _draft.ScreenshotFormat = SelectedComboTag(ScreenshotFormatBox);
        _draft.CaptureScreenshotCursor = ScreenshotCursorSwitch.IsChecked == true;
        _draft.VideoFormat = SelectedComboTag(VideoFormatBox);
        _draft.CaptureVideoCursor = VideoCursorSwitch.IsChecked == true;
        _draft.JpgQuality = (int)JpgQualitySlider.Value;
        _draft.AfterSaveAction = SelectedComboTag(AfterSaveBox);
        _draft.UpdateInterval = SelectedComboTag(UpdateIntervalBox);

        _draft.NotificationsEnabled   = NotifyMasterSwitch.IsChecked    == true;
        _draft.NotifyScreenshotSaved  = NotifyScreenshotSwitch.IsChecked == true;
        _draft.NotifyVideoSaved       = NotifyVideoSwitch.IsChecked     == true;
        _draft.NotifyClipboard        = NotifyClipboardSwitch.IsChecked == true;
        _draft.NotifyErrors           = NotifyErrorsSwitch.IsChecked    == true;
        _draft.NotifyUpdateAvailable  = NotifyUpdateSwitch.IsChecked    == true;
        _draft.NotifyHints            = NotifyHintsSwitch.IsChecked     == true;

        _draft.VideoCodec = SelectedRadioTag(RadioCodecH264, RadioCodecH265, RadioCodecVp9, RadioCodecAv1);
        _draft.VideoResolution = SelectedSegmentTag(ResBtn480p, ResBtn720p, ResBtn1080p, ResBtn1440p, ResBtnOriginal);
        _draft.VideoBitrateMbps = (int)BitrateSlider.Value;

        _draft.MicrophoneEnabled = MicEnabledSwitch.IsChecked == true;
        _draft.MicrophoneDevice  = (MicDeviceBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

        _draft.GifColors = (int)GifColorSlider.Value;
        _draft.GifFps = (int)GifFpsSlider.Value;
        _draft.GifDither = GifDitherSwitch.IsChecked == true;

        foreach (var row in _hotkeyRows)
        {
            ApplyHotkey(row.Key, row.Binding);
        }
    }

    private static void SelectSegment(string tag, params ToggleButton[] btns)
    {
        bool any = false;
        foreach (var btn in btns)
        {
            bool match = string.Equals(btn.Tag as string, tag, StringComparison.OrdinalIgnoreCase);
            btn.IsChecked = match;
            if (match) any = true;
        }
        if (!any && btns.Length > 0) btns[0].IsChecked = true;
    }

    private static string SelectedSegmentTag(params ToggleButton[] btns)
    {
        foreach (var btn in btns)
            if (btn.IsChecked == true) return (btn.Tag as string) ?? string.Empty;
        return btns.Length > 0 ? (btns[0].Tag as string) ?? string.Empty : string.Empty;
    }

    private static void SelectRadio(string tag, params RadioButton[] radios)
    {
        bool any = false;
        foreach (var r in radios)
        {
            if (string.Equals(r.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                r.IsChecked = true;
                any = true;
                break;
            }
        }
        if (!any && radios.Length > 0) radios[0].IsChecked = true;
    }

    private static string SelectedRadioTag(params RadioButton[] radios)
    {
        foreach (var r in radios)
            if (r.IsChecked == true) return (r.Tag as string) ?? string.Empty;
        return radios.Length > 0 ? (radios[0].Tag as string) ?? string.Empty : string.Empty;
    }

    private static string GetBuildDate()
    {
        try
        {
            var loc = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrEmpty(loc))
                return string.Format(Strings.Get("LblBuilt"), File.GetLastWriteTime(loc).ToString("d MMM yyyy"));
        }
        catch { }
        return string.Empty;
    }

    private async void OnScreenshotFolderPick(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync(ScreenshotFolderBox.Text);
        if (!string.IsNullOrEmpty(path)) ScreenshotFolderBox.Text = path;
    }

    private async void OnVideoFolderPick(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync(VideoFolderBox.Text);
        if (!string.IsNullOrEmpty(path)) VideoFolderBox.Text = path;
    }

    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string initialDir)
    {
        // SHBrowseForFolderW on a Task.Run threadpool thread is non-STA,
        // which made the dialog leak modal state to the parent window
        // (clicks blocked, ding sound). FolderPicker is fully UI-thread,
        // async, and respects the WinUI 3 dispatcher.
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("SettingsWindow.PickFolderAsync", ex);
            return null;
        }
    }

    private void OnThemeSegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        foreach (var btn in new[] { ThemeBtnAuto, ThemeBtnDark, ThemeBtnLight })
            btn.IsChecked = btn == clicked;
        MarkChanged();
    }

    private void OnScreenshotFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateJpgQualityRowVisibility();
        if (!_loading) MarkChanged();
    }

    private void UpdateJpgQualityRowVisibility()
    {
        if (JpgQualityRow == null || ScreenshotFormatBox == null) return;
        var tag = SelectedComboTag(ScreenshotFormatBox);
        JpgQualityRow.Visibility = tag == "jpg" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnJpgQualityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (JpgQualityLabel != null) JpgQualityLabel.Text = ((int)JpgQualitySlider.Value).ToString();
        if (!_loading) MarkChanged();
    }

    // ============== Change tracking ==============

    private void OnAnyControlChanged(object sender, SelectionChangedEventArgs e) => MarkChanged();
    private void OnAnyControlChanged(object sender, RoutedEventArgs e) => MarkChanged();

    private void OnAnyTextChanged(object sender, TextChangedEventArgs e) => MarkChanged();
    private void OnAnyToggleChanged(object sender, RoutedEventArgs e) => MarkChanged();

    private void OnAutostartToggled(object sender, RoutedEventArgs e)
    {
        // Defer apply until Save so the change participates in dirty tracking
        // and can be discarded by Close → "discard?" prompt.
        MarkChanged();
    }

    private void OnNotifyMasterToggled(object sender, RoutedEventArgs e)
    {
        UpdateNotifySubPanelState();
        MarkChanged();
    }

    private void UpdateNotifySubPanelState()
    {
        bool on = NotifyMasterSwitch.IsChecked == true;
        NotifySubPanel.Opacity = on ? 1.0 : 0.4;
        NotifySubPanel.IsHitTestVisible = on;
    }

    private void MarkChanged()
    {
        if (_loading) return;
        Collect();
        ComputeDirty();
        UpdateDirtyVisuals();
    }

    private void ComputeDirty()
    {
        _dirty.Clear();
        if (_draft.Language != _initial.Language) _dirty.Add("lang");
        if (_draft.Theme != _initial.Theme) _dirty.Add("theme");
        if (_draft.OcrEngine != _initial.OcrEngine) _dirty.Add("ocr");
        if (_draft.TesseractLanguages != _initial.TesseractLanguages) _dirty.Add("ocr");
        if (_draft.TranslateService != _initial.TranslateService) _dirty.Add("translate-svc");
        if (_draft.TranslateFrom    != _initial.TranslateFrom)    _dirty.Add("translate-from");
        if (_draft.TranslateTo      != _initial.TranslateTo)      _dirty.Add("translate-to");
        if (_draft.ScreenshotFolder != _initial.ScreenshotFolder) _dirty.Add("ss-folder");
        if (_draft.VideoFolder != _initial.VideoFolder) _dirty.Add("vid-folder");
        if (_draft.RememberLastFolder != _initial.RememberLastFolder) _dirty.Add("remember");
        if ((AutostartSwitch.IsChecked == true) != _initialAutostart) _dirty.Add("autostart");
        if (_draft.ScreenshotFormat != _initial.ScreenshotFormat) _dirty.Add("ss-format");
        if (_draft.CaptureScreenshotCursor != _initial.CaptureScreenshotCursor) _dirty.Add("ss-cursor");
        if (_draft.VideoFormat != _initial.VideoFormat) _dirty.Add("vid-format");
        if (_draft.CaptureVideoCursor != _initial.CaptureVideoCursor) _dirty.Add("vid-cursor");
        if (_draft.JpgQuality != _initial.JpgQuality) _dirty.Add("jpg-q");
        if (_draft.AfterSaveAction != _initial.AfterSaveAction) _dirty.Add("after-save");
        if (_draft.UpdateInterval != _initial.UpdateInterval) _dirty.Add("update-int");
        if (_draft.NotificationsEnabled  != _initial.NotificationsEnabled  ||
            _draft.NotifyScreenshotSaved != _initial.NotifyScreenshotSaved ||
            _draft.NotifyVideoSaved      != _initial.NotifyVideoSaved      ||
            _draft.NotifyClipboard       != _initial.NotifyClipboard       ||
            _draft.NotifyErrors          != _initial.NotifyErrors          ||
            _draft.NotifyUpdateAvailable != _initial.NotifyUpdateAvailable ||
            _draft.NotifyHints           != _initial.NotifyHints)
            _dirty.Add("notif");
        if (_draft.VideoCodec != _initial.VideoCodec) _dirty.Add("codec");
        if (_draft.VideoResolution != _initial.VideoResolution) _dirty.Add("resolution");
        if (_draft.VideoBitrateMbps != _initial.VideoBitrateMbps) _dirty.Add("bitrate");
        if (_draft.GifColors != _initial.GifColors) _dirty.Add("gif-color");
        if (_draft.GifFps != _initial.GifFps) _dirty.Add("gif-fps");
        if (_draft.GifDither != _initial.GifDither) _dirty.Add("gif-dither");
        if (_draft.HotkeyCapture != _initial.HotkeyCapture) _dirty.Add("hk-capture");
        if (_draft.HotkeyScreenshotSilent != _initial.HotkeyScreenshotSilent) _dirty.Add("hk-save-silent");
        if (_draft.HotkeyCopy != _initial.HotkeyCopy) _dirty.Add("hk-copy");
        if (_draft.HotkeyUndo != _initial.HotkeyUndo) _dirty.Add("hk-undo");
        if (_draft.HotkeyRedo != _initial.HotkeyRedo) _dirty.Add("hk-redo");
        if (_draft.HotkeySelectAll != _initial.HotkeySelectAll) _dirty.Add("hk-select-all");
        if (_draft.HotkeyRecordSilentSave != _initial.HotkeyRecordSilentSave) _dirty.Add("hk-record-save");
        if (_draft.HotkeyMicToggle != _initial.HotkeyMicToggle) _dirty.Add("hk-mic-toggle");
        if (_draft.MicrophoneEnabled != _initial.MicrophoneEnabled) _dirty.Add("mic-enabled");
        if (_draft.MicrophoneDevice  != _initial.MicrophoneDevice)  _dirty.Add("mic-device");
    }

    private void UpdateDirtyVisuals()
    {
        bool any = _dirty.Count > 0;
        FooterStatusText.Text = any ? Strings.Get("NotifyUnsaved") : string.Empty;
        BtnSave.IsEnabled = any;

        SetLabel(LblLanguage, "LblLanguage", _dirty.Contains("lang"));
        SetLabel(LblTheme, "LblTheme", _dirty.Contains("theme"));
        SetLabel(LblOcrEngine, "LblOcrEngine", _dirty.Contains("ocr"));
        SetLabel(LblTranslateService, "LblTranslateService",
            _dirty.Contains("translate-svc") || _dirty.Contains("translate-from") || _dirty.Contains("translate-to"));
        SetLabel(LblScreenshotFolder, "LblScreenshotFolder", _dirty.Contains("ss-folder"));
        SetLabel(LblVideoFolder, "LblVideoFolder", _dirty.Contains("vid-folder"));
        SetLabel(LblRememberFolder, "LblRememberFolder", _dirty.Contains("remember"));
        SetLabel(LblAutostart, "LblAutostart", _dirty.Contains("autostart"));
        SetLabel(LblScreenshotFormat, "LblScreenshotFormat", _dirty.Contains("ss-format"));
        SetLabel(LblScreenshotCursor, "LblScreenshotCursor", _dirty.Contains("ss-cursor"));
        SetLabel(LblVideoFormat, "LblVideoFormat", _dirty.Contains("vid-format"));
        SetLabel(LblVideoCursor, "LblVideoCursor", _dirty.Contains("vid-cursor"));
        SetLabel(LblJpgQuality, "LblJpgQuality", _dirty.Contains("jpg-q"));
        SetLabel(LblAfterSave, "LblAfterSave", _dirty.Contains("after-save"));
        SetLabel(LblUpdates, "LblUpdates", _dirty.Contains("update-int"));
        SetLabel(LblNotifyMaster, "LblNotifyMaster", _dirty.Contains("notif"));
        SetLabel(LblCodec, "LblCodec", _dirty.Contains("codec"));
        SetLabel(LblResolution, "LblResolution", _dirty.Contains("resolution"));
        SetLabel(LblBitrate, "LblBitrate", _dirty.Contains("bitrate"));
        SetLabel(LblGifColors, "LblGifColors", _dirty.Contains("gif-color"));
        SetLabel(LblGifFps, "LblGifFps", _dirty.Contains("gif-fps"));
        SetLabel(LblGifDither, "LblGifDither", _dirty.Contains("gif-dither"));

        SetNavLabel(NavGeneralLabel, "TabGeneral", "general");
        SetNavLabel(NavVideoLabel,   "TabVideo",   "video");
        SetNavLabel(NavOcrLabel,     "TabOcr",     "ocr");
        SetNavLabel(NavGifLabel,     "TabGif",     "gif");
        SetNavLabel(NavHotkeysLabel, "TabHotkeys", "hotkeys");
        SetNavLabel(NavInfoLabel,    "TabInfo",    "info");
    }

    private static void SetLabel(TextBlock lbl, string stringKey, bool dirty)
    {
        if (lbl == null) return;
        var baseText = Strings.Get(stringKey);
        WriteDirtyLabel(lbl, baseText, dirty);
    }

    private void SetNavLabel(TextBlock lbl, string stringKey, string category)
    {
        if (lbl == null) return;
        var baseText = Strings.Get(stringKey);
        bool catDirty = false;
        foreach (var k in _dirty)
        {
            var c = k.StartsWith("hk-") ? "hotkeys" : _paramToCategory.GetValueOrDefault(k, string.Empty);
            if (c == category) { catDirty = true; break; }
        }
        WriteDirtyLabel(lbl, baseText, catDirty);
    }

    private static void WriteDirtyLabel(TextBlock lbl, string baseText, bool dirty)
    {
        lbl.Inlines.Clear();
        lbl.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = baseText });
        if (dirty)
        {
            // Orange dot rendered after the label sits at the trailing corner of
            // the param / nav row, so changed items pop without obscuring the text.
            var warn = (Brush)Application.Current.Resources["ClipsyWarningBrush"];
            lbl.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = "  ●",
                Foreground = warn,
            });
        }
    }

    // ============== Notification ==============

    private void ShowNotification(string messageKey, string kind = "success")
        => ShowNotificationText(Strings.Get(messageKey), kind);

    private void ShowNotificationText(string text, string kind)
    {
        NotifyMessage.Text = text;
        string glyph;
        string brushKey;
        switch (kind)
        {
            case "error":   glyph = ""; brushKey = "ClipsyDangerBrush"; break;
            case "warning": glyph = ""; brushKey = "ClipsyWarningBrush"; break;
            case "info":    glyph = ""; brushKey = "ClipsyAccentBrush"; break;
            default:        glyph = ""; brushKey = "ClipsySuccessBrush"; break;
        }
        NotifyIcon.Glyph = glyph;
        try
        {
            var brush = (Brush)Application.Current.Resources[brushKey];
            NotifyIcon.Foreground = brush;
            NotifyAccentBar.Background = brush;
        }
        catch { }
        NotifyBanner.Visibility = Visibility.Visible;

        _notifyTimer?.Stop();
        _notifyTimer = DispatcherQueue.CreateTimer();
        _notifyTimer.Interval = TimeSpan.FromSeconds(3);
        _notifyTimer.IsRepeating = false;
        _notifyTimer.Tick += (_, _) => { NotifyBanner.Visibility = Visibility.Collapsed; };
        _notifyTimer.Start();
    }

    // ============== Sidebar tip rotation ==============

    private void StartTipRotation()
    {
        if (_tipTimer != null || _tipKeys.Length == 0) return;
        ReshuffleTips(-1);
        AdvanceTip(animate: false);
        _tipTimer = DispatcherQueue.CreateTimer();
        _tipTimer.Interval = TimeSpan.FromSeconds(8);
        _tipTimer.IsRepeating = true;
        _tipTimer.Tick += (_, _) => AdvanceTip(animate: true);
        _tipTimer.Start();
    }

    // Fisher-Yates shuffle. avoidFirst keeps the same tip from showing twice in
    // a row across a cycle boundary.
    private void ReshuffleTips(int avoidFirst)
    {
        _tipOrder.Clear();
        for (int i = 0; i < _tipKeys.Length; i++) _tipOrder.Add(i);
        for (int i = _tipOrder.Count - 1; i > 0; i--)
        {
            int j = _tipRng.Next(i + 1);
            (_tipOrder[i], _tipOrder[j]) = (_tipOrder[j], _tipOrder[i]);
        }
        if (avoidFirst >= 0 && _tipOrder.Count > 1 && _tipOrder[0] == avoidFirst)
            (_tipOrder[0], _tipOrder[1]) = (_tipOrder[1], _tipOrder[0]);
        _tipPos = -1;
    }

    private void AdvanceTip(bool animate)
    {
        if (LblTip == null) return;
        _tipPos++;
        if (_tipPos >= _tipOrder.Count)
        {
            ReshuffleTips(_currentTipKeyIndex);
            _tipPos = 0;
        }
        _currentTipKeyIndex = _tipOrder[_tipPos];
        var text = Strings.Get(_tipKeys[_currentTipKeyIndex]);

        if (!animate)
        {
            LblTip.Text = text;
            LblTip.Opacity = 1;
            return;
        }

        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(fadeOut, LblTip);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        var sbOut = new Storyboard();
        sbOut.Children.Add(fadeOut);
        sbOut.Completed += (_, _) =>
        {
            LblTip.Text = text;
            var fadeIn = new DoubleAnimation
            {
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(fadeIn, LblTip);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");
            var sbIn = new Storyboard();
            sbIn.Children.Add(fadeIn);
            sbIn.Begin();
        };
        sbOut.Begin();
    }

    // ============== Save / Reset / Updates ==============

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            Collect();
            SettingsService.Instance.Replace(_draft);
            bool wantAutostart = AutostartSwitch.IsChecked == true;
            if (wantAutostart != _initialAutostart)
            {
                AutostartService.SetEnabled(wantAutostart);
                _initialAutostart = AutostartService.IsEnabled();
                AutostartSwitch.IsChecked = _initialAutostart;
            }
            _initial = _draft.Clone();
            _dirty.Clear();
            ThemeService.ApplyTo(Content as FrameworkElement);
            ApplyLocalization();
            UpdateDirtyVisuals();
            ShowNotification("NotifySaved", "success");
        }
        catch (Exception ex)
        {
            Diagnostics.Log("SettingsWindow.OnSave", ex);
            ShowNotification("NotifySaveFailed", "error");
        }
    }

    private async void OnClose(object sender, RoutedEventArgs e)
    {
        if (_dirty.Count > 0 && !await ConfirmDiscardChanges()) return;
        Close();
    }

    private async void OnReset(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmReset()) return;
        // Persist defaults right away — previously Reset only updated the UI
        // draft, leaving SettingsService unchanged until the user also hit Save.
        _draft = new AppSettings();
        SettingsService.Instance.Replace(_draft);
        // Autostart isn't part of AppSettings; default = off.
        AutostartService.SetEnabled(false);
        Load();
        ThemeService.ApplyTo(Content as FrameworkElement);
        ApplyLocalization();
        ShowNotification("NotifyReset", "info");
    }

    private Task<bool> ConfirmDiscardChanges() =>
        ShowConfirmAsync(Strings.Get("ConfirmDiscardTitle"), Strings.Get("ConfirmDiscardBody"));

    private Task<bool> ConfirmReset() =>
        ShowConfirmAsync(Strings.Get("ConfirmResetTitle"), Strings.Get("ConfirmResetBody"));

    private async Task<bool> ShowConfirmAsync(string title, string body)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = Strings.Get("BtnConfirm"),
            CloseButtonText = Strings.Get("BtnCancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        try
        {
            ShowNotification("NotifyUpdateChecking", "info");
            var info = await UpdateService.CheckLatestAsync();
            if (info == null)
            {
                ShowNotification("NotifyUpdateFailed", "error");
                return;
            }
            if (UpdateService.IsNewer(info.Version, UpdateService.CurrentVersion()))
            {
                ShowNotificationText(string.Format(Strings.Get("NotifyUpdateAvailable"), info.Version), "info");
            }
            else
            {
                ShowNotification("NotifyUpdateUpToDate", "success");
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("SettingsWindow.OnCheckUpdates", ex);
            ShowNotification("NotifyUpdateFailed", "error");
        }
    }

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;
        if (PaneGeneral is null) return;
        var key = rb.Tag as string;

        PaneGeneral.Visibility = key == "general" ? Visibility.Visible : Visibility.Collapsed;
        PaneVideo.Visibility   = key == "video"   ? Visibility.Visible : Visibility.Collapsed;
        PaneOcr.Visibility     = key == "ocr"     ? Visibility.Visible : Visibility.Collapsed;
        PaneGif.Visibility     = key == "gif"     ? Visibility.Visible : Visibility.Collapsed;
        PaneHotkeys.Visibility       = key == "hotkeys"       ? Visibility.Visible : Visibility.Collapsed;
        PaneNotifications.Visibility = key == "notifications" ? Visibility.Visible : Visibility.Collapsed;
        PaneInfo.Visibility          = key == "info"          ? Visibility.Visible : Visibility.Collapsed;

        try
        {
            var accent = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ClipsyAccentBrush"];
            var dim = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ClipsyText2Brush"];
            IconNavGeneral.Foreground = key == "general" ? accent : dim;
            IconNavVideo.Foreground   = key == "video"   ? accent : dim;
            IconNavOcr.Foreground     = key == "ocr"     ? accent : dim;
            IconNavGif.Foreground     = key == "gif"     ? accent : dim;
            IconNavHotkeys.Foreground       = key == "hotkeys"       ? accent : dim;
            IconNavNotifications.Foreground = key == "notifications" ? accent : dim;
            IconNavInfo.Foreground          = key == "info"          ? accent : dim;
        }
        catch { }
    }
}
