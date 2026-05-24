using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Clipsy.Localization;
using Clipsy.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Clipsy.Views.Settings;

public sealed partial class SettingsWindow : Window
{
    public sealed class HotkeyRow : System.ComponentModel.INotifyPropertyChanged
    {
        public required string Key { get; init; }

        private string _label = string.Empty;
        public required string Label
        {
            get => _label;
            set { if (_label != value) { _label = value; OnChanged(); } }
        }

        private string _binding = string.Empty;
        public string Binding
        {
            get => _binding;
            set { if (_binding != value) { _binding = value; OnChanged(); } }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name ?? string.Empty));
    }

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
        ["after-save"] = "general",
        ["update-int"] = "general",
        ["translate-svc"]  = "ocr",
        ["translate-from"] = "ocr",
        ["translate-to"]   = "ocr",
        ["codec"] = "video",
        ["resolution"] = "video",
        ["bitrate"] = "video",
        ["vid-format"] = "video",
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
        Closed += (_, _) => { if (_current == this) _current = null; };
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
            foreach (var rb in new[] { NavGeneral, NavVideo, NavOcr, NavGif, NavHotkeys, NavInfo })
            {
                if (rb.IsChecked == true) { OnNavChecked(rb, new RoutedEventArgs()); break; }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Show("SettingsWindow.OnFirstActivated", ex);
        }
    }

    private void ApplyLocalization()
    {
        NavGeneralLabel.Text  = Strings.Get("TabGeneral");
        NavVideoLabel.Text    = Strings.Get("TabVideo");
        NavOcrLabel.Text      = Strings.Get("TabOcr");
        NavGifLabel.Text      = Strings.Get("TabGif");
        NavHotkeysLabel.Text  = Strings.Get("TabHotkeys");
        NavInfoLabel.Text     = Strings.Get("TabInfo");

        if (TitleBarSubtitle != null) TitleBarSubtitle.Text = Strings.Get("TitleBarSubtitle");
        if (LblTipHeader != null)     LblTipHeader.Text    = Strings.Get("TipLabel");
        if (LblTip != null)           LblTip.Text          = Strings.Get("TipText");

        HdrGeneral.Text  = Strings.Get("TabGeneral");
        HdrVideo.Text    = Strings.Get("TabVideo");
        HdrOcr.Text      = Strings.Get("TabOcr");
        HdrGif.Text      = Strings.Get("TabGif");
        HdrHotkeys.Text  = Strings.Get("TabHotkeys");

        SubGeneral.Text  = Strings.Get("SubGeneral");
        SubVideo.Text    = Strings.Get("SubVideo");
        SubOcr.Text      = Strings.Get("SubOcr");
        SubGif.Text      = Strings.Get("SubGif");

        HelperLanguage.Text  = Strings.Get("HelperLanguage");
        HelperTheme.Text     = Strings.Get("HelperTheme");
        HelperOcr.Text       = Strings.Get("HelperOcr");
        LblTessLang.Text     = Strings.Get("LblTessLang");
        HelperTessLang.Text  = Strings.Get("HelperTessLang");
        LblTranslateService.Text = Strings.Get("LblTranslateService");
        HelperTranslation.Text   = Strings.Get("HelperTranslation");
        LblTranslateFrom.Text    = Strings.Get("LblTranslateFrom");
        LblTranslateTo.Text      = Strings.Get("LblTranslateTo");
        BuildTranslateLangDropdowns();
        HelperRemember.Text  = Strings.Get("HelperRemember");
        HelperCodec.Text    = Strings.Get("HelperCodec");
        HelperBitrate.Text  = Strings.Get("HelperBitrate");
        HelperGifColors.Text= Strings.Get("HelperGifColors");
        HelperGifFps.Text   = Strings.Get("HelperGifFps");
        HelperGifDither.Text= Strings.Get("HelperGifDither");

        LblLanguage.Text         = Strings.Get("LblLanguage");
        LblTheme.Text            = Strings.Get("LblTheme");
        LblOcrEngine.Text        = Strings.Get("LblOcrEngine");
        LblScreenshotFolder.Text = Strings.Get("LblScreenshotFolder");
        LblVideoFolder.Text      = Strings.Get("LblVideoFolder");
        LblRememberFolder.Text   = Strings.Get("LblRememberFolder");
        LblAutostart.Text        = Strings.Get("LblAutostart");
        HelperAutostart.Text     = Strings.Get("HelperAutostart");
        LblScreenshotFormat.Text = Strings.Get("LblScreenshotFormat");
        LblVideoFormat.Text      = Strings.Get("LblVideoFormat");
        LblJpgQuality.Text       = Strings.Get("LblJpgQuality");
        LblAfterSave.Text        = Strings.Get("LblAfterSave");
        LblUpdates.Text          = Strings.Get("LblUpdates");

        if (LblAuthor != null)        LblAuthor.Text        = Strings.Get("LblAuthorHeader");
        if (LblMit != null)           LblMit.Text           = Strings.Get("LblMit");
        if (LblGithubLine != null)    LblGithubLine.Text    = Strings.Get("LblGithubLine");
        if (LinkGithubOpen != null)   LinkGithubOpen.Content = Strings.Get("BtnOpen");
        if (LblLikeClipsy != null)    LblLikeClipsy.Text    = Strings.Get("LblLikeClipsy");
        if (LblLikeClipsyHint != null) LblLikeClipsyHint.Text = Strings.Get("LblLikeClipsyHint");
        if (LblStarBtn != null)       LblStarBtn.Text       = Strings.Get("BtnStar");
        if (UpdateStatusLabel != null) UpdateStatusLabel.Text = Strings.Get("LblUpdateStatus");

        LangAuto.Content   = Strings.Get("OptAuto");
        LangEn.Content     = Strings.Get("OptEnglish");
        LangRu.Content     = Strings.Get("OptRussian");
        ThemeBtnAutoLabel.Text  = Strings.Get("OptAuto");
        ThemeBtnDarkLabel.Text  = Strings.Get("OptDark");
        ThemeBtnLightLabel.Text = Strings.Get("OptLight");
        var defaultSuffix = " " + Strings.Get("SuffixDefault");
        OcrTesseract.Content = Strings.Get("OptTesseract");
        // WinRT is the OCR engine default — append a localized "(default)" hint.
        OcrWinRt.Content   = Strings.Get("OptWinRtOcr") + defaultSuffix;
        TrSvcMyMemory.Content = Strings.Get("OptMyMemory");
        TrSvcGoogle.Content   = Strings.Get("OptGoogle") + defaultSuffix;
        FmtPng.Content     = Strings.Get("OptPngLossless");
        FmtJpg.Content     = Strings.Get("OptJpgSmaller");
        FmtWebp.Content    = Strings.Get("OptWebpPreview");
        AfterNothing.Content   = Strings.Get("OptDoNothing");
        AfterOpenFile.Content  = Strings.Get("OptOpenFile");
        AfterOpenFolder.Content= Strings.Get("OptOpenFolder");
        UpdHourly.Content  = Strings.Get("OptHourly");
        UpdDaily.Content   = Strings.Get("OptDaily");
        UpdWeekly.Content  = Strings.Get("OptWeekly");
        UpdMonthly.Content = Strings.Get("OptMonthly");
        UpdNever.Content   = Strings.Get("OptNever");

        LblCodec.Text      = Strings.Get("LblCodec");
        LblResolution.Text = Strings.Get("LblResolution");
        LblBitrate.Text    = Strings.Get("LblBitrate");
        LblRegionNote.Text = Strings.Get("LblRegionNote");
        UpdateFfmpegSection();

        LblGifColors.Text  = Strings.Get("LblGifColors");
        LblGifFps.Text     = Strings.Get("LblGifFps");
        LblGifDither.Text = Strings.Get("LblGifDither");

        LblHotkeyHint.Text = Strings.Get("LblHotkeyHint");

        BtnCheckForUpdates.Content = Strings.Get("BtnCheckForUpdates");
        ScreenshotFolderPick.Content = Strings.Get("BtnBrowse");
        VideoFolderPick.Content      = Strings.Get("BtnBrowse");
        BtnCheckNow.Content          = Strings.Get("BtnCheckNow");
        BtnReset.Content             = Strings.Get("BtnReset");
        BtnClose.Content             = Strings.Get("BtnClose");
        BtnSave.Content              = Strings.Get("BtnSave");

        // Rebuild hotkey rows with localized labels (preserves bindings).
        var wasLoading = _loading;
        _loading = true;
        BuildHotkeyRows();

        // WinUI ComboBox caches the SelectedItem's rendered content, so
        // mutating ComboBoxItem.Content above updates dropdown items but
        // leaves the collapsed display showing the old text. Kick each box
        // by toggling SelectedIndex so the ContentPresenter re-renders.
        RefreshComboDisplay(LangBox);
        RefreshComboDisplay(ScreenshotFormatBox);
        RefreshComboDisplay(AfterSaveBox);
        RefreshComboDisplay(UpdateIntervalBox);
        RefreshComboDisplay(VideoFormatBox);
        RefreshComboDisplay(OcrEngineBox);
        RefreshComboDisplay(TranslateServiceBox);
        _loading = wasLoading;
    }

    private static void RefreshComboDisplay(ComboBox? cb)
    {
        if (cb == null) return;
        int idx = cb.SelectedIndex;
        if (idx < 0) return;
        cb.SelectedIndex = -1;
        cb.SelectedIndex = idx;
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
        SelectComboByTag(VideoFormatBox, _draft.VideoFormat);

        JpgQualitySlider.Minimum = 50;
        JpgQualitySlider.Maximum = 100;
        JpgQualitySlider.Value = System.Math.Clamp(_draft.JpgQuality, 50, 100);
        JpgQualityLabel.Text = ((int)JpgQualitySlider.Value).ToString();
        UpdateJpgQualityRowVisibility();

        SelectComboByTag(AfterSaveBox, _draft.AfterSaveAction);
        SelectComboByTag(UpdateIntervalBox, _draft.UpdateInterval);

        SelectRadio(_draft.VideoCodec, RadioCodecH264, RadioCodecH265, RadioCodecVp9, RadioCodecAv1);
        SelectSegment(_draft.VideoResolution, ResBtn480p, ResBtn720p, ResBtn1080p, ResBtn1440p, ResBtnOriginal);
        UpdateBitrateBounds(_draft.VideoResolution);
        BitrateSlider.Value = System.Math.Clamp(_draft.VideoBitrateMbps, (int)BitrateSlider.Minimum, (int)BitrateSlider.Maximum);
        UpdateBitrateLabel();

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
        _draft.VideoFormat = SelectedComboTag(VideoFormatBox);
        _draft.JpgQuality = (int)JpgQualitySlider.Value;
        _draft.AfterSaveAction = SelectedComboTag(AfterSaveBox);
        _draft.UpdateInterval = SelectedComboTag(UpdateIntervalBox);

        _draft.VideoCodec = SelectedRadioTag(RadioCodecH264, RadioCodecH265, RadioCodecVp9, RadioCodecAv1);
        _draft.VideoResolution = SelectedSegmentTag(ResBtn480p, ResBtn720p, ResBtn1080p, ResBtn1440p, ResBtnOriginal);
        _draft.VideoBitrateMbps = (int)BitrateSlider.Value;

        _draft.GifColors = (int)GifColorSlider.Value;
        _draft.GifFps = (int)GifFpsSlider.Value;
        _draft.GifDither = GifDitherSwitch.IsChecked == true;

        foreach (var row in _hotkeyRows)
        {
            ApplyHotkey(row.Key, row.Binding);
        }
    }

    private void ApplyHotkey(string key, string binding)
    {
        switch (key)
        {
            case "capture": _draft.HotkeyCapture = binding; break;
            case "save-silent": _draft.HotkeyScreenshotSilent = binding; break;
            case "copy": _draft.HotkeyCopy = binding; break;
            case "undo": _draft.HotkeyUndo = binding; break;
            case "redo": _draft.HotkeyRedo = binding; break;
            case "select-all": _draft.HotkeySelectAll = binding; break;
            case "record-save": _draft.HotkeyRecordSilentSave = binding; break;
        }
    }

    private void BuildHotkeyRows()
    {
        foreach (var existing in _hotkeyRows) existing.PropertyChanged -= OnHotkeyRowChanged;
        _hotkeyRows.Clear();
        AddHotkeyRow("capture",     "HkOpenCapture", _draft.HotkeyCapture);
        AddHotkeyRow("save-silent", "HkSaveSilent",  _draft.HotkeyScreenshotSilent);
        AddHotkeyRow("copy",        "HkCopy",        _draft.HotkeyCopy);
        AddHotkeyRow("undo",        "HkUndo",        _draft.HotkeyUndo);
        AddHotkeyRow("redo",        "HkRedo",        _draft.HotkeyRedo);
        AddHotkeyRow("select-all",  "HkSelectAll",   _draft.HotkeySelectAll);
        AddHotkeyRow("record-save", "HkRecordSave",  _draft.HotkeyRecordSilentSave);
    }

    private void AddHotkeyRow(string key, string labelKey, string binding)
    {
        var row = new HotkeyRow { Key = key, Label = Strings.Get(labelKey), Binding = binding };
        row.PropertyChanged += OnHotkeyRowChanged;
        _hotkeyRows.Add(row);
    }

    private void OnHotkeyRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_loading) return;
        if (e.PropertyName == nameof(HotkeyRow.Binding)) MarkChanged();
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
        return await System.Threading.Tasks.Task.Run(() =>
        {
            const int BIF_RETURNONLYFSDIRS = 0x0001;
            const int BIF_NEWDIALOGSTYLE = 0x0040;
            var bi = new BROWSEINFO
            {
                hwndOwner = _hwnd,
                pszDisplayName = Marshal.AllocHGlobal(260 * 2),
                lpszTitle = "Choose folder",
                ulFlags = BIF_RETURNONLYFSDIRS | BIF_NEWDIALOGSTYLE,
            };
            IntPtr pidl = SHBrowseForFolderW(ref bi);
            string? result = null;
            if (pidl != IntPtr.Zero)
            {
                var buf = Marshal.AllocHGlobal(260 * 2);
                if (SHGetPathFromIDListW(pidl, buf))
                {
                    result = Marshal.PtrToStringUni(buf);
                }
                Marshal.FreeHGlobal(buf);
                Marshal.FreeCoTaskMem(pidl);
            }
            Marshal.FreeHGlobal(bi.pszDisplayName);
            return result;
        });
    }

    private void OnThemeSegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        foreach (var btn in new[] { ThemeBtnAuto, ThemeBtnDark, ThemeBtnLight })
            btn.IsChecked = btn == clicked;
        MarkChanged();
    }

    private void OnResolutionSegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        foreach (var btn in new[] { ResBtn480p, ResBtn720p, ResBtn1080p, ResBtn1440p, ResBtnOriginal })
            btn.IsChecked = btn == clicked;
        UpdateBitrateBounds(clicked.Tag as string ?? string.Empty);
        UpdateBitrateLabel();
        MarkChanged();
    }

    private void OnScreenshotFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateJpgQualityRowVisibility();
    }

    private void OnVideoFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) MarkChanged();
    }

    // ============== FFmpeg ==============

    private void UpdateFfmpegSection()
    {
        if (LblFfmpegSection == null) return;
        bool installed = FFmpegService.Instance.IsAvailable;

        LblFfmpegSection.Text   = Strings.Get("LblFfmpegSection");
        HelperCodecVp9Av1.Text  = Strings.Get("HelperCodecVp9Av1");
        FfmpegStatusText.Text   = Strings.Get(installed ? "FfmpegInstalled" : "FfmpegNotInstalled");
        BtnInstallFfmpeg.Content = Strings.Get("BtnInstallFfmpeg");
        BtnDeleteFfmpeg.Content  = Strings.Get("BtnDeleteFfmpeg");
        BtnCancelFfmpeg.Content  = Strings.Get("BtnCancelFfmpeg");

        try
        {
            var fg = (Brush)Application.Current.Resources[installed ? "ClipsySuccessBrush" : "ClipsyText3Brush"];
            var bg = (Brush)Application.Current.Resources["ClipsyBg2Brush"];
            var bd = (Brush)Application.Current.Resources["ClipsyBorderSubtleBrush"];
            FfmpegStatusText.Foreground   = fg;
            FfmpegStatusBadge.Background  = bg;
            FfmpegStatusBadge.BorderBrush = bd;
        }
        catch { }

        BtnInstallFfmpeg.Visibility  = installed ? Visibility.Collapsed : Visibility.Visible;
        BtnDeleteFfmpeg.Visibility   = installed ? Visibility.Visible   : Visibility.Collapsed;
        BtnCancelFfmpeg.Visibility   = Visibility.Collapsed;
        FfmpegProgressRow.Visibility = Visibility.Collapsed;

        UpdateCodecRadioAvailability();
    }

    private void UpdateCodecRadioAvailability()
    {
        if (RadioCodecVp9 == null || RadioCodecAv1 == null) return;
        bool ffmpegAvailable = FFmpegService.Instance.IsAvailable;
        RadioCodecVp9.IsEnabled = ffmpegAvailable;
        RadioCodecAv1.IsEnabled = ffmpegAvailable;

        // Force off VP9/AV1 if FFmpeg not available
        if (!ffmpegAvailable)
        {
            var codec = SelectedRadioTag(RadioCodecH264, RadioCodecH265, RadioCodecVp9, RadioCodecAv1);
            if (codec == "VP9" || codec == "AV1")
            {
                var wasLoading = _loading;
                _loading = true;
                RadioCodecH264.IsChecked = true;
                _loading = wasLoading;
            }
        }
    }

    private async void OnFfmpegInstall(object sender, RoutedEventArgs e)
    {
        _ffmpegCts?.Cancel();
        _ffmpegCts = new CancellationTokenSource();
        var cts = _ffmpegCts;

        BtnInstallFfmpeg.Visibility  = Visibility.Collapsed;
        BtnCancelFfmpeg.Visibility   = Visibility.Visible;
        FfmpegProgressRow.Visibility = Visibility.Visible;
        FfmpegProgressBar.Value      = 0;
        FfmpegProgressText.Text      = Strings.Get("FfmpegDownloading");

        var progress = new Progress<(int Percent, string Message)>(p =>
            DispatcherQueue.TryEnqueue(() =>
            {
                FfmpegProgressBar.Value = p.Percent;
                FfmpegProgressText.Text = p.Message;
            }));

        bool ok = await FFmpegService.Instance.DownloadAsync(progress, cts.Token);

        if (!cts.IsCancellationRequested)
            ShowNotification(ok ? "FfmpegDone" : "ErrFfmpegFailed", ok ? "success" : "error");

        UpdateFfmpegSection();
    }

    private void OnFfmpegDelete(object sender, RoutedEventArgs e)
    {
        FFmpegService.Instance.Delete();
        UpdateFfmpegSection();
    }

    private void OnFfmpegCancel(object sender, RoutedEventArgs e)
    {
        _ffmpegCts?.Cancel();
        _ffmpegCts = null;
        UpdateFfmpegSection();
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

    private void UpdateBitrateBounds(string resolution)
    {
        int max = resolution switch
        {
            "480p" => 4,
            "720p" => 8,
            "1080p" => 16,
            "1440p" => 32,
            "Original" => 50,
            _ => 16,
        };
        BitrateSlider.Maximum = max;
        if (BitrateSlider.Value < 1) BitrateSlider.Value = 1;
        BitrateSlider.Minimum = 1;
        if (BitrateSlider.Value > max) BitrateSlider.Value = max;
    }

    private void OnBitrateChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateBitrateLabel();
        if (!_loading) MarkChanged();
    }

    private void UpdateBitrateLabel()
    {
        if (BitrateLabel == null || EstFileSizeLabel == null || BitrateSlider == null) return;
        int mbps = (int)BitrateSlider.Value;
        BitrateLabel.Text = string.Format(Strings.Get("BitrateMbps"), mbps);
        double mbPerMin = mbps * 60.0 / 8.0;
        long rounded = (long)System.Math.Round(mbPerMin / 10.0) * 10;
        if (rounded == 0) rounded = 10;
        EstFileSizeLabel.Text = string.Format(Strings.Get("BitrateEstimate"), rounded);
    }

    private void OnGifColorChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (GifColorLabel != null) GifColorLabel.Text = ((int)GifColorSlider.Value).ToString();
    }

    private void OnGifFpsChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (GifFpsLabel != null) GifFpsLabel.Text = ((int)GifFpsSlider.Value).ToString();
    }

    private void OnHotkeyRebindClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string key) return;
        if (_listeningButton == b)
        {
            FinishListening();
            return;
        }
        if (_listeningButton != null) FinishListening();
        _listeningButton = b;
        _listeningKey = key;
        b.Content = Strings.Get("HkPressKeys");
        try
        {
            b.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ClipsyBg2Brush"];
            b.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ClipsyAccentBrush"];
        }
        catch { }
        Content.KeyDown -= OnRebindKeyDown;
        Content.KeyDown += OnRebindKeyDown;
    }

    private void OnRebindKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_listeningButton == null || _listeningKey == null) return;
        if (e.Key == VirtualKey.Escape)
        {
            FinishListening();
            e.Handled = true;
            return;
        }
        if (IsModifierOnly(e.Key)) { e.Handled = true; return; }
        var binding = ChordToString(e.Key);
        var row = _hotkeyRows.FirstOrDefault(r => r.Key == _listeningKey);
        if (row != null) row.Binding = binding;
        _listeningButton.Content = binding;
        FinishListening();
        e.Handled = true;
    }

    private void FinishListening()
    {
        if (_listeningButton != null)
        {
            var row = _hotkeyRows.FirstOrDefault(r => r.Key == _listeningKey);
            if (row != null && (_listeningButton.Content as string) == Strings.Get("HkPressKeys"))
            {
                _listeningButton.Content = row.Binding;
            }
            try
            {
                _listeningButton.ClearValue(Microsoft.UI.Xaml.Controls.Control.BackgroundProperty);
                _listeningButton.ClearValue(Microsoft.UI.Xaml.Controls.Control.BorderBrushProperty);
            }
            catch { }
        }
        _listeningButton = null;
        _listeningKey = null;
        Content.KeyDown -= OnRebindKeyDown;
    }

    private static bool IsModifierOnly(VirtualKey key)
    {
        return key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
            or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.LeftMenu or VirtualKey.RightMenu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows;
    }

    private static string ChordToString(VirtualKey key)
    {
        var parts = new List<string>();
        if (IsDown(VirtualKey.Control)) parts.Add("Ctrl");
        if (IsDown(VirtualKey.Shift)) parts.Add("Shift");
        if (IsDown(VirtualKey.Menu)) parts.Add("Alt");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private static bool IsDown(VirtualKey k)
    {
        var s = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k);
        return (s & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }

    // ============== Change tracking ==============

    private void OnAnyControlChanged(object sender, SelectionChangedEventArgs e) => MarkChanged();
    private void OnAnyControlChanged(object sender, RoutedEventArgs e) => MarkChanged();

    private void OnOcrEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTessLangSectionVisibility();
        MarkChanged();
    }

    private void UpdateTessLangSectionVisibility()
    {
        if (TessLangSection == null) return;
        var isTesseract = string.Equals(SelectedComboTag(OcrEngineBox), "Tesseract", StringComparison.OrdinalIgnoreCase);
        TessLangSection.Visibility = isTesseract ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildTessLangRows()
    {
        TessLangList.Children.Clear();
        foreach (var lang in TessdataService.Catalog)
            TessLangList.Children.Add(CreateTessLangRow(lang));
    }

    private UIElement CreateTessLangRow(TessdataLang lang)
    {
        var installed = TessdataService.IsInstalled(lang.Code);

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // One row, one action. Install / Delete is the only control; an
        // installed language is automatically used for OCR, so there is no
        // separate "selected" checkbox.
        CheckBox? cb = null; // signature compatibility with DownloadTessLangAsync
        var nameBlock = new TextBlock
        {
            Text = lang.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["ClipsyBody"],
            Opacity = installed ? 1.0 : 0.7,
        };
        Grid.SetColumn(nameBlock, 0);

        // Size label
        var sizeBlock = new TextBlock
        {
            Text = lang.ApproxSize,
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["ClipsyHelper"],
            Margin = new Thickness(8, 0, 8, 0),
        };
        Grid.SetColumn(sizeBlock, 1);

        // Progress bar (hidden by default)
        var progress = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Value = 0,
            Width = 80,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(progress, 2);

        // Action button
        var btn = new Button
        {
            Content = installed ? Strings.Get("BtnDelete") : Strings.Get("BtnInstall"),
            Style = (Style)Application.Current.Resources[installed ? "ClipsyButtonGhost" : "ClipsyButtonGhost"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        Grid.SetColumn(btn, 3);

        grid.Children.Add(nameBlock);
        grid.Children.Add(sizeBlock);
        grid.Children.Add(progress);
        grid.Children.Add(btn);

        // Button handler
        btn.Click += (_, _) =>
        {
            if (TessdataService.IsInstalled(lang.Code))
            {
                TessdataService.Delete(lang.Code);
                _tessSelectedCodes.Remove(lang.Code);
                MarkChanged();
                // Rebuild this row
                var idx = TessLangList.Children.IndexOf(grid);
                if (idx >= 0) TessLangList.Children[idx] = CreateTessLangRow(lang);
            }
            else
            {
                _ = DownloadTessLangAsync(lang, grid, btn, progress, cb);
            }
        };

        return grid;
    }

    private async Task DownloadTessLangAsync(TessdataLang lang, Grid row, Button btn, ProgressBar progressBar, CheckBox? cb)
    {
        if (_tessDownloadCts.TryGetValue(lang.Code, out var existing))
        {
            existing.Cancel();
            _tessDownloadCts.Remove(lang.Code);
        }

        var cts = new CancellationTokenSource();
        _tessDownloadCts[lang.Code] = cts;

        btn.IsEnabled = false;
        btn.Content = Strings.Get("TessInstalling");
        progressBar.Visibility = Visibility.Visible;
        progressBar.Value = 0;

        try
        {
            var p = new Progress<int>(v => DispatcherQueue.TryEnqueue(() => progressBar.Value = v));
            await TessdataService.DownloadAsync(lang.Code, p, cts.Token);

            // Success — auto-select the new language
            _tessSelectedCodes.Add(lang.Code);
            MarkChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Tessdata download failed: {ex.Message}");
            NotificationService.Error("ErrTessDownload");
        }
        finally
        {
            _tessDownloadCts.Remove(lang.Code);
            // Rebuild the row to reflect new state
            DispatcherQueue.TryEnqueue(() =>
            {
                var idx = TessLangList.Children.IndexOf(row);
                if (idx >= 0) TessLangList.Children[idx] = CreateTessLangRow(lang);
            });
        }
    }
    private void BuildTranslateLangDropdowns()
    {
        var prevFrom = SelectedComboTag(TranslateFromBox);
        var prevTo   = SelectedComboTag(TranslateToBox);

        TranslateFromBox.Items.Clear();
        TranslateToBox.Items.Clear();

        bool ru = Strings.Lang == "ru";

        // "From" dropdown: Auto-detect + all languages
        TranslateFromBox.Items.Add(new ComboBoxItem
        {
            Content = Strings.Get("LangAutoDetect"),
            Tag = "auto",
        });
        // "To" dropdown: Interface language (default) + all languages
        TranslateToBox.Items.Add(new ComboBoxItem
        {
            Content = Strings.Get("LangUiDefault"),
            Tag = "ui",
        });

        foreach (var lang in TranslationService.LangCatalog)
        {
            var name = ru ? lang.Ru : lang.En;
            TranslateFromBox.Items.Add(new ComboBoxItem { Content = name, Tag = lang.Code });
            TranslateToBox.Items.Add(new ComboBoxItem   { Content = name, Tag = lang.Code });
        }

        // Restore previous selection
        SelectComboByTag(TranslateFromBox, string.IsNullOrEmpty(prevFrom) ? "auto" : prevFrom);
        SelectComboByTag(TranslateToBox,   string.IsNullOrEmpty(prevTo)   ? "ui"   : prevTo);
    }

    private void OnAnyTextChanged(object sender, TextChangedEventArgs e) => MarkChanged();
    private void OnAnyToggleChanged(object sender, RoutedEventArgs e) => MarkChanged();

    private void OnAutostartToggled(object sender, RoutedEventArgs e)
    {
        // Defer apply until Save so the change participates in dirty tracking
        // and can be discarded by Close → "discard?" prompt.
        MarkChanged();
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
        if (_draft.VideoFormat != _initial.VideoFormat) _dirty.Add("vid-format");
        if (_draft.JpgQuality != _initial.JpgQuality) _dirty.Add("jpg-q");
        if (_draft.AfterSaveAction != _initial.AfterSaveAction) _dirty.Add("after-save");
        if (_draft.UpdateInterval != _initial.UpdateInterval) _dirty.Add("update-int");
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
        SetLabel(LblVideoFormat, "LblVideoFormat", _dirty.Contains("vid-format"));
        SetLabel(LblJpgQuality, "LblJpgQuality", _dirty.Contains("jpg-q"));
        SetLabel(LblAfterSave, "LblAfterSave", _dirty.Contains("after-save"));
        SetLabel(LblUpdates, "LblUpdates", _dirty.Contains("update-int"));
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
            case "error":   glyph = ""; brushKey = "ClipsyDangerBrush"; break;
            case "warning": glyph = ""; brushKey = "ClipsyWarningBrush"; break;
            case "info":    glyph = ""; brushKey = "ClipsyAccentBrush"; break;
            default:        glyph = ""; brushKey = "ClipsySuccessBrush"; break;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BROWSEINFO
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BROWSEINFO lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, IntPtr pszPath);

    private void OnNavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;
        if (PaneGeneral is null) return;
        var key = rb.Tag as string;

        PaneGeneral.Visibility = key == "general" ? Visibility.Visible : Visibility.Collapsed;
        PaneVideo.Visibility   = key == "video"   ? Visibility.Visible : Visibility.Collapsed;
        PaneOcr.Visibility     = key == "ocr"     ? Visibility.Visible : Visibility.Collapsed;
        PaneGif.Visibility     = key == "gif"     ? Visibility.Visible : Visibility.Collapsed;
        PaneHotkeys.Visibility = key == "hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        PaneInfo.Visibility    = key == "info"    ? Visibility.Visible : Visibility.Collapsed;

        try
        {
            var accent = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ClipsyAccentBrush"];
            var dim = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ClipsyText2Brush"];
            IconNavGeneral.Foreground = key == "general" ? accent : dim;
            IconNavVideo.Foreground   = key == "video"   ? accent : dim;
            IconNavOcr.Foreground     = key == "ocr"     ? accent : dim;
            IconNavGif.Foreground     = key == "gif"     ? accent : dim;
            IconNavHotkeys.Foreground = key == "hotkeys" ? accent : dim;
            IconNavInfo.Foreground    = key == "info"    ? accent : dim;
        }
        catch { }
    }
}