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
    // Pool-of-one: a warmed hidden spare plus the visible window. Showing a
    // freshly-warmed window is flash-free; reusing one after hide is not.
    private static SettingsWindow? _spare;
    private static SettingsWindow? _open;

    private readonly IntPtr _hwnd;
    private AppWindow? _appWindow;
    private AppSettings _draft;
    private AppSettings _initial = new();
    private readonly ObservableCollection<HotkeyRow> _hotkeyRows = new();
    private readonly HashSet<string> _dirty = new();
    private Button? _listeningButton;
    private string? _listeningKey;
    private bool _setupDone;
    // True until Load() applies the initial draft, so handlers firing during
    // InitializeComponent don't MarkChanged() before the tree is populated.
    private bool _loading = true;

    // Tessdata language management
    private readonly HashSet<string> _tessSelectedCodes = new();
    private readonly Dictionary<string, CancellationTokenSource> _tessDownloadCts = new();

    // FFmpeg download
    private CancellationTokenSource? _ffmpegCts;

    // Autostart is a scheduled task (not AppSettings) — track init state separately.
    private bool _initialAutostart;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _notifyTimer;

    // Sidebar tip rotation: a shuffled queue of tip keys cycled on a timer with
    // a fade swap. Not tied to the active tab — purely ambient.
    private static readonly string[] _tipKeys =
    {
        "TipSelectScreenDouble",
        "TipSelectAll",
        "TipOcrEngine",
        "TipOcrLang",
        "TipTranslate",
        "TipEyedropper",
        "TipResizeHandles",
        "TipCopySaveKeys",
        "TipClearDrawings",
        "TipEscCancel",
        "TipHotkeyRebind",
        "TipRecordRegion",
        "TipMicToggle",
        "TipSilentSave",
        "TipGifExport",
        "TipGifSize",
        "TipFfmpeg",
        "TipJpgQuality",
        "TipAutostart",
        "TipAfterSave",
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
        ["dyn-islands"] = "general",
        ["after-save"] = "general",
        ["update-int"] = "general",
        ["notif"] = "notifications",
        ["translate-svc"]  = "ocr",
        ["translate-from"] = "ocr",
        ["translate-to"]   = "ocr",
        ["codec"] = "video",
        ["resolution"] = "video",
        ["framerate"] = "video",
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
        PresetToggles();

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        catch (Exception ex)
        {
            Diagnostics.Log("SettingsWindow.SetTitleBar", ex);
        }

        // Tool-window so the off-screen warm render never flashes a taskbar button.
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        try
        {
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
        }
        catch { }

        Closed += (_, _) =>
        {
            _tipTimer?.Stop();
            SettingsService.Instance.SettingsChanged -= OnGlobalSettingsChanged;
            UpdateManager.StateChanged -= RenderUpdateStatus;
            if (_open == this) _open = null;
        };

        UpdateManager.StateChanged += RenderUpdateStatus;
        RenderUpdateStatus();

        // Keep the warmed-but-hidden spare's strings/colors in sync with changes
        // from the open window, else reopening flashes the stale language/theme.
        SettingsService.Instance.SettingsChanged += OnGlobalSettingsChanged;

        // Re-apply nav CheckStates once the tree is live (initial pass runs while
        // most radios are still null and before the theme is applied).
        if (Content is FrameworkElement rootFe)
            rootFe.Loaded += (_, _) => SnapNavVisuals();
    }

    // Re-localize + re-tint a hidden/open window when settings change elsewhere.
    // Guarded on _setupDone so the spare isn't touched before its tree is built.
    private void OnGlobalSettingsChanged()
    {
        if (!_setupDone) return;
        try
        {
            ApplyLocalization();
            RefreshNavIcons();
        }
        catch (Exception ex) { Diagnostics.Log("SettingsWindow.OnGlobalSettingsChanged", ex); }
    }

    // One-time heavy init: title bar theming, content Load, nav. Runs during
    // warm-up so the window is fully built before it's ever shown.
    private void SetupOnce()
    {
        if (_setupDone) return;
        _setupDone = true;
        try
        {
            _appWindow!.Title = Strings.Get("TraySettings");
            _appWindow.Resize(new SizeInt32(WinW, WinH));
            try
            {
                var tb = _appWindow.TitleBar;
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
            foreach (var rb in new[] { NavGeneral, NavVideo, NavOcr, NavGif, NavHotkeys, NavNotifications, NavInfo })
                if (rb.IsChecked == true) { OnNavChecked(rb, new RoutedEventArgs()); break; }
        }
        catch (Exception ex) { Diagnostics.Show("SettingsWindow.SetupOnce", ex); }
    }

    // Composite the XAML once off-screen without activating (Show(false)) so the
    // swapchain is warm and the later Reveal appears instantly, no black frame.
    private void Warm()
    {
        SetupOnce();
        try
        {
            _appWindow!.MoveAndResize(new RectInt32(OffScreen, OffScreen, WinW, WinH));
            _appWindow.Show(false);
        }
        catch (Exception ex) { Diagnostics.Log("SettingsWindow.Warm", ex); }
    }

    // Bring this already-warm window on-screen, centered, focused.
    private void Reveal()
    {
        try
        {
            // Refresh values in case settings changed since warm-up.
            _draft = SettingsService.Instance.Settings.Clone();
            Load();
            // Re-localize/re-tint: the spare's labels may be stale from warm time.
            ApplyLocalization();
            RefreshNavIcons();
            // Promote tool-window → app window so it shows in taskbar/Alt+Tab.
            // The taskbar button re-evaluates only while hidden, so toggle around it.
            try
            {
                _appWindow?.Hide();
                int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
                ex = (ex & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW;
                SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
            }
            catch (Exception ex) { Diagnostics.Log("SettingsWindow.Reveal promote", ex); }
            _appWindow?.Move(new Windows.Graphics.PointInt32(CenterX(), CenterY()));
            Activate();
            SetForegroundWindow(_hwnd);
            StartTipRotation();
            MaybeShowChangelogAfterUpdate();
        }
        catch (Exception ex) { Diagnostics.Show("SettingsWindow.Reveal", ex); }
    }

    private Windows.Graphics.RectInt32 WorkArea() =>
        DisplayArea.GetFromWindowId(
            Win32Interop.GetWindowIdFromWindow(_hwnd), DisplayAreaFallback.Primary).WorkArea;
    private int CenterX() { var w = WorkArea(); return w.X + (w.Width  - WinW) / 2; }
    private int CenterY() { var w = WorkArea(); return w.Y + (w.Height - WinH) / 2; }

    private const int OffScreen = -32000;
    private const int WinW = 940, WinH = 640;
    private const int GWL_EXSTYLE      = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_APPWINDOW  = 0x00040000;
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr h, int n);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr h, int n, int v);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);

    // Warm a spare hidden instance (off the user's critical path). Show(false)
    // doesn't steal focus, so this is safe to call any time.
    public static void Prewarm()
    {
        if (_spare != null) return;
        try { var w = new SettingsWindow(); w.Warm(); _spare = w; }
        catch (Exception ex) { Diagnostics.Show("SettingsWindow.Prewarm", ex); }
    }

    public static void ShowOrActivate()
    {
        try
        {
            if (_open != null)
            {
                _open.Activate();
                _open.BringToFront();
                return;
            }
            if (_spare == null) Prewarm();
            _open = _spare;
            _spare = null;
            _open?.Reveal();
            Prewarm(); // refill the spare for next time
        }
        catch (Exception ex) { Diagnostics.Show("SettingsWindow.ShowOrActivate", ex); }
    }

    private void BringToFront() => SetForegroundWindow(_hwnd);

    private static string GetVersion()
    {
        return UpdateService.CurrentVersion();
    }

    // Seed toggles pre-load (before they enter the visual tree) so they snap
    // without the knob-slide; Load() re-applies the same values, no animation.
    private void PresetToggles()
    {
        _initialAutostart = AutostartService.IsEnabled();
        RememberFolderSwitch.IsChecked   = _draft.RememberLastFolder;
        AutostartSwitch.IsChecked        = _initialAutostart;
        ScreenshotCursorSwitch.IsChecked = _draft.CaptureScreenshotCursor;
        DynamicIslandsSwitch.IsChecked   = _draft.DynamicToolbarIslands;
        VideoCursorSwitch.IsChecked      = _draft.CaptureVideoCursor;
        MicEnabledSwitch.IsChecked       = _draft.MicrophoneEnabled;
        GifDitherSwitch.IsChecked        = _draft.GifDither;
        NotifyMasterSwitch.IsChecked     = _draft.NotificationsEnabled;
        NotifyScreenshotSwitch.IsChecked = _draft.NotifyScreenshotSaved;
        NotifyVideoSwitch.IsChecked      = _draft.NotifyVideoSaved;
        NotifyClipboardSwitch.IsChecked  = _draft.NotifyClipboard;
        NotifyErrorsSwitch.IsChecked     = _draft.NotifyErrors;
        NotifyUpdateSwitch.IsChecked     = _draft.NotifyUpdateAvailable;
        NotifyHintsSwitch.IsChecked      = _draft.NotifyHints;
        AutoDownloadSwitch.IsChecked     = _draft.AutoDownloadUpdates;
    }

    private void Load()
    {
        _loading = true;
        SelectComboByTag(LangBox, _draft.Language);
        SelectSegment(_draft.Theme, ThemeBtnAuto, ThemeBtnDark, ThemeBtnLight);
        SelectComboByTag(OcrEngineBox, _draft.OcrEngine);
        _tessSelectedCodes.Clear();
        // Every installed language is used for OCR; persisted codes merged in
        // for forward-compat with older configs.
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
        // Mirror displayed defaults back into the draft so the _initial snapshot
        // matches Collect(), else the folder rows falsely mark dirty after Load().
        _draft.ScreenshotFolder = ScreenshotFolderBox.Text;
        _draft.VideoFolder      = VideoFolderBox.Text;
        RememberFolderSwitch.IsChecked = _draft.RememberLastFolder;
        _initialAutostart = AutostartService.IsEnabled();
        AutostartSwitch.IsChecked = _initialAutostart;

        SelectComboByTag(ScreenshotFormatBox, _draft.ScreenshotFormat);
        ScreenshotCursorSwitch.IsChecked = _draft.CaptureScreenshotCursor;
        DynamicIslandsSwitch.IsChecked   = _draft.DynamicToolbarIslands;
        SelectComboByTag(VideoFormatBox, _draft.VideoFormat);
        VideoCursorSwitch.IsChecked = _draft.CaptureVideoCursor;

        JpgQualitySlider.Minimum = 50;
        JpgQualitySlider.Maximum = 100;
        JpgQualitySlider.Value = System.Math.Clamp(_draft.JpgQuality, 50, 100);
        JpgQualityLabel.Text = ((int)JpgQualitySlider.Value).ToString();
        UpdateJpgQualityRowVisibility();

        SelectComboByTag(AfterSaveBox, _draft.AfterSaveAction);
        SelectComboByTag(EyedropperModBox, _draft.EyedropperModifier);
        SelectComboByTag(UpdateIntervalBox, _draft.UpdateInterval);
        AutoDownloadSwitch.IsChecked = _draft.AutoDownloadUpdates;

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
        SelectSegment(_draft.VideoFramerate.ToString(), FpsBtn60, FpsBtn15, FpsBtn30, FpsBtnNative);
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
        _draft.DynamicToolbarIslands = DynamicIslandsSwitch.IsChecked == true;
        _draft.VideoFormat = SelectedComboTag(VideoFormatBox);
        _draft.CaptureVideoCursor = VideoCursorSwitch.IsChecked == true;
        _draft.JpgQuality = (int)JpgQualitySlider.Value;
        _draft.AfterSaveAction = SelectedComboTag(AfterSaveBox);
        _draft.EyedropperModifier = SelectedComboTag(EyedropperModBox);
        _draft.UpdateInterval = SelectedComboTag(UpdateIntervalBox);
        _draft.AutoDownloadUpdates = AutoDownloadSwitch.IsChecked == true;

        _draft.NotificationsEnabled   = NotifyMasterSwitch.IsChecked    == true;
        _draft.NotifyScreenshotSaved  = NotifyScreenshotSwitch.IsChecked == true;
        _draft.NotifyVideoSaved       = NotifyVideoSwitch.IsChecked     == true;
        _draft.NotifyClipboard        = NotifyClipboardSwitch.IsChecked == true;
        _draft.NotifyErrors           = NotifyErrorsSwitch.IsChecked    == true;
        _draft.NotifyUpdateAvailable  = NotifyUpdateSwitch.IsChecked    == true;
        _draft.NotifyHints            = NotifyHintsSwitch.IsChecked     == true;

        _draft.VideoCodec = SelectedRadioTag(RadioCodecH264, RadioCodecH265, RadioCodecVp9, RadioCodecAv1);
        _draft.VideoResolution = SelectedSegmentTag(ResBtn480p, ResBtn720p, ResBtn1080p, ResBtn1440p, ResBtnOriginal);
        _draft.VideoFramerate = int.TryParse(SelectedSegmentTag(FpsBtn15, FpsBtn30, FpsBtn60, FpsBtnNative), out var vfps) ? vfps : 60;
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

    private System.Threading.Tasks.Task<string?> PickFolderAsync(string initialDir)
    {
        // Win32 picker (runs elevated, unlike the broker-hosted WinRT FolderPicker).
        return SaveDialogService.PickFolderAsync(_hwnd);
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
        if (_draft.DynamicToolbarIslands != _initial.DynamicToolbarIslands) _dirty.Add("dyn-islands");
        if (_draft.VideoFormat != _initial.VideoFormat) _dirty.Add("vid-format");
        if (_draft.CaptureVideoCursor != _initial.CaptureVideoCursor) _dirty.Add("vid-cursor");
        if (_draft.JpgQuality != _initial.JpgQuality) _dirty.Add("jpg-q");
        if (_draft.AfterSaveAction != _initial.AfterSaveAction) _dirty.Add("after-save");
        if (_draft.EyedropperModifier != _initial.EyedropperModifier) _dirty.Add("eyedropper-mod");
        if (_draft.UpdateInterval != _initial.UpdateInterval) _dirty.Add("update-int");
        if (_draft.AutoDownloadUpdates != _initial.AutoDownloadUpdates) _dirty.Add("auto-dl");
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
        if (_draft.VideoFramerate != _initial.VideoFramerate) _dirty.Add("framerate");
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
        SetLabel(LblDynamicIslands, "LblDynamicIslands", _dirty.Contains("dyn-islands"));
        SetLabel(LblVideoFormat, "LblVideoFormat", _dirty.Contains("vid-format"));
        SetLabel(LblVideoCursor, "LblVideoCursor", _dirty.Contains("vid-cursor"));
        SetLabel(LblJpgQuality, "LblJpgQuality", _dirty.Contains("jpg-q"));
        SetLabel(LblAfterSave, "LblAfterSave", _dirty.Contains("after-save"));
        SetLabel(LblUpdates, "LblUpdates", _dirty.Contains("update-int"));
        SetLabel(LblNotifyMaster, "LblNotifyMaster", _dirty.Contains("notif"));
        SetLabel(LblCodec, "LblCodec", _dirty.Contains("codec"));
        SetLabel(LblResolution, "LblResolution", _dirty.Contains("resolution"));
        SetLabel(LblVideoFps, "LblVideoFps", _dirty.Contains("framerate"));
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
            var warn = ThemeService.GetBrush("ClipsyWarningBrush", lbl);
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
            // Theme may have changed: re-tint nav icons once ActualTheme settles.
            DispatcherQueue.TryEnqueue(RefreshNavIcons);
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
        DispatcherQueue.TryEnqueue(RefreshNavIcons);
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

    private void OnCheckUpdates(object sender, RoutedEventArgs e)
        => _ = UpdateManager.CheckAsync(true);

    private void OnOpenChangelog(object sender, RoutedEventArgs e)
        => ChangelogWindow.ShowWindow();

    // First Settings open after an update auto-shows the changelog once.
    private void MaybeShowChangelogAfterUpdate()
    {
        var current = UpdateService.CurrentVersion();
        var s = SettingsService.Instance.Settings;
        if (s.LastChangelogVersion == current) return;
        s.LastChangelogVersion = current;
        SettingsService.Instance.Save();
        ChangelogWindow.ShowWindow();
    }

    private void OnUpdateActionClick(object sender, RoutedEventArgs e)
        => UpdateManager.PrimaryAction();

    // Mirrors the tray update button: shows phase, download percent, and a
    // Download/Install action.
    private void RenderUpdateStatus()
    {
        try
        {
            switch (UpdateManager.Phase)
            {
                case UpdatePhase.Available:
                    UpdateStatusRow.Visibility = Visibility.Visible;
                    UpdProgress.Visibility = Visibility.Collapsed;
                    UpdStatusText.Text = Strings.Get("TrayUpdateAvailable");
                    SetUpdActionText(Strings.Get("ToastDownload"));
                    break;
                case UpdatePhase.Downloading:
                    UpdateStatusRow.Visibility = Visibility.Visible;
                    UpdProgress.Visibility = Visibility.Visible;
                    UpdProgress.Value = System.Math.Clamp(UpdateManager.Progress * 100.0, 0, 100);
                    UpdStatusText.Text = $"{Strings.Get("TrayUpdateDownloading")} {(int)(UpdateManager.Progress * 100)}%";
                    UpdActionBtn.Visibility = Visibility.Collapsed;
                    break;
                case UpdatePhase.Ready:
                    UpdateStatusRow.Visibility = Visibility.Visible;
                    UpdProgress.Visibility = Visibility.Collapsed;
                    UpdStatusText.Text = Strings.Get("TrayUpdateInstall");
                    SetUpdActionText(Strings.Get("ToastInstallNow"));
                    break;
                default: // None / Checking / UpToDate / Failed
                    UpdateStatusRow.Visibility = Visibility.Collapsed;
                    break;
            }
        }
        catch (Exception ex) { Diagnostics.Log("SettingsWindow.RenderUpdateStatus", ex); }
    }

    private void SetUpdActionText(string text)
    {
        UpdActionBtn.Visibility = Visibility.Visible;
        UpdActionBtn.Content = text;
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

        SnapNavVisuals();

        FrameworkElement? shown = key switch
        {
            "general"       => PaneGeneral,
            "video"         => PaneVideo,
            "ocr"           => PaneOcr,
            "gif"           => PaneGif,
            "hotkeys"       => PaneHotkeys,
            "notifications" => PaneNotifications,
            "info"          => PaneInfo,
            _               => null,
        };
        if (shown != null)
        {
            SnapToggles(shown);
            FadeInPane(shown);
        }

        RefreshNavIcons();
    }

    // Re-tint sidebar icons (imperatively colored) for the current selection +
    // theme; must run on every theme change or their brushes go stale grey.
    private void RefreshNavIcons()
    {
        string? key = null;
        foreach (var rb in new[] { NavGeneral, NavVideo, NavOcr, NavGif, NavHotkeys, NavNotifications, NavInfo })
            if (rb?.IsChecked == true) { key = rb.Tag as string; break; }

        try
        {
            var accent = ThemeService.GetBrush("ClipsyAccentBrush", Content as FrameworkElement);
            var dim = ThemeService.GetBrush("ClipsyText2Brush", Content as FrameworkElement);
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

    // Force nav radios' CheckStates: markup IsChecked isn't always applied, and
    // the style Foreground resolves against the app theme until a state re-applies.
    private void SnapNavVisuals()
    {
        foreach (var rb in new[] { NavGeneral, NavVideo, NavOcr, NavGif, NavHotkeys, NavNotifications, NavInfo })
        {
            if (rb == null) continue;
            VisualStateManager.GoToState(rb, rb.IsChecked == true ? "Checked" : "Unchecked", false);
        }
    }

    // Snap toggles in a freshly-revealed pane to their final state with no
    // transition — storyboards deferred while the pane was Collapsed else replay.
    private static void SnapToggles(DependencyObject root)
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (child is RadioButton rb)
            {
                // RadioButton keeps Checked/Unchecked in its own CheckStates group.
                bool on = rb.IsChecked == true;
                VisualStateManager.GoToState(rb, on ? "Unchecked" : "Checked", false);
                VisualStateManager.GoToState(rb, on ? "Checked" : "Unchecked", false);
            }
            else if (child is ToggleButton tb)
            {
                bool on = tb.IsChecked == true;
                // Flip to the opposite state then back, both transition-less, to
                // force an instant re-apply (GoToState no-ops if already in state).
                VisualStateManager.GoToState(tb, on ? "Normal" : "Checked", false);
                VisualStateManager.GoToState(tb, on ? "Checked" : "Normal", false);
            }
            SnapToggles(child);
        }
    }

    // Quick fade-in when a settings pane becomes visible — softens the
    // section switch without delaying it. Opacity only (composition prop).
    private static void FadeInPane(FrameworkElement pane)
    {
        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(120)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
            },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, pane);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(fade);
        sb.Begin();
    }
}
