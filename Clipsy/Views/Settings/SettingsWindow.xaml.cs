using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
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
    private bool _loading;

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _notifyTimer;

    private static readonly Dictionary<string, string> _paramToCategory = new()
    {
        ["lang"] = "general",
        ["theme"] = "general",
        ["ocr"] = "general",
        ["ss-folder"] = "general",
        ["vid-folder"] = "general",
        ["remember"] = "general",
        ["ss-format"] = "general",
        ["jpg-q"] = "general",
        ["after-save"] = "general",
        ["update-int"] = "general",
        ["codec"] = "video",
        ["resolution"] = "video",
        ["bitrate"] = "video",
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

        Activated += OnFirstActivated;
        Closed += (_, _) => { if (_current == this) _current = null; };
    }


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
            foreach (var rb in new[] { NavGeneral, NavVideo, NavGif, NavHotkeys, NavInfo })
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
        NavGifLabel.Text      = Strings.Get("TabGif");
        NavHotkeysLabel.Text  = Strings.Get("TabHotkeys");
        NavInfoLabel.Text     = Strings.Get("TabInfo");

        if (TitleBarSubtitle != null) TitleBarSubtitle.Text = Strings.Get("TitleBarSubtitle");
        if (LblTipHeader != null)     LblTipHeader.Text    = Strings.Get("TipLabel");
        if (LblTip != null)           LblTip.Text          = Strings.Get("TipText");

        HdrGeneral.Text  = Strings.Get("TabGeneral");
        HdrVideo.Text    = Strings.Get("TabVideo");
        HdrGif.Text      = Strings.Get("TabGif");
        HdrHotkeys.Text  = Strings.Get("TabHotkeys");

        SubGeneral.Text  = Strings.Get("SubGeneral");
        SubVideo.Text    = Strings.Get("SubVideo");
        SubGif.Text      = Strings.Get("SubGif");

        HelperLanguage.Text = Strings.Get("HelperLanguage");
        HelperTheme.Text    = Strings.Get("HelperTheme");
        HelperOcr.Text      = Strings.Get("HelperOcr");
        HelperRemember.Text = Strings.Get("HelperRemember");
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
        LblScreenshotFormat.Text = Strings.Get("LblScreenshotFormat");
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
        OcrTesseract.Content = Strings.Get("OptTesseract");
        OcrWinRt.Content   = Strings.Get("OptWinRtOcr");
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
        _loading = wasLoading;
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
        ScreenshotFolderBox.Text = string.IsNullOrEmpty(_draft.ScreenshotFolder)
            ? SettingsService.Instance.DefaultScreenshotFolder
            : _draft.ScreenshotFolder!;
        VideoFolderBox.Text = string.IsNullOrEmpty(_draft.VideoFolder)
            ? SettingsService.Instance.DefaultVideoFolder
            : _draft.VideoFolder!;
        RememberFolderSwitch.IsChecked = _draft.RememberLastFolder;

        SelectComboByTag(ScreenshotFormatBox, _draft.ScreenshotFormat);

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
        _draft.ScreenshotFolder = ScreenshotFolderBox.Text;
        _draft.VideoFolder = VideoFolderBox.Text;
        _draft.RememberLastFolder = RememberFolderSwitch.IsChecked == true;
        _draft.ScreenshotFormat = SelectedComboTag(ScreenshotFormatBox);
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

    private void UpdateJpgQualityRowVisibility()
    {
        if (JpgQualityRow == null || ScreenshotFormatBox == null) return;
        var tag = SelectedComboTag(ScreenshotFormatBox);
        JpgQualityRow.Visibility = tag == "jpg" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnJpgQualityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (JpgQualityLabel != null) JpgQualityLabel.Text = ((int)JpgQualitySlider.Value).ToString();
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
    private void OnAnyTextChanged(object sender, TextChangedEventArgs e) => MarkChanged();
    private void OnAnyToggleChanged(object sender, RoutedEventArgs e) => MarkChanged();

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
        if (_draft.ScreenshotFolder != _initial.ScreenshotFolder) _dirty.Add("ss-folder");
        if (_draft.VideoFolder != _initial.VideoFolder) _dirty.Add("vid-folder");
        if (_draft.RememberLastFolder != _initial.RememberLastFolder) _dirty.Add("remember");
        if (_draft.ScreenshotFormat != _initial.ScreenshotFormat) _dirty.Add("ss-format");
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
        DotUnsaved.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        FooterStatusText.Text = any ? Strings.Get("NotifyUnsaved") : string.Empty;

        SetLabel(LblLanguage, "LblLanguage", _dirty.Contains("lang"));
        SetLabel(LblTheme, "LblTheme", _dirty.Contains("theme"));
        SetLabel(LblOcrEngine, "LblOcrEngine", _dirty.Contains("ocr"));
        SetLabel(LblScreenshotFolder, "LblScreenshotFolder", _dirty.Contains("ss-folder"));
        SetLabel(LblVideoFolder, "LblVideoFolder", _dirty.Contains("vid-folder"));
        SetLabel(LblRememberFolder, "LblRememberFolder", _dirty.Contains("remember"));
        SetLabel(LblScreenshotFormat, "LblScreenshotFormat", _dirty.Contains("ss-format"));
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
        SetNavLabel(NavGifLabel,     "TabGif",     "gif");
        SetNavLabel(NavHotkeysLabel, "TabHotkeys", "hotkeys");
        SetNavLabel(NavInfoLabel,    "TabInfo",    "info");
    }

    private static void SetLabel(TextBlock lbl, string stringKey, bool dirty)
    {
        if (lbl == null) return;
        var baseText = Strings.Get(stringKey);
        lbl.Text = (dirty ? "● " : string.Empty) + baseText;
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
        lbl.Text = (catDirty ? "● " : string.Empty) + baseText;
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

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _draft = new AppSettings();
        Load();
        ThemeService.ApplyTo(Content as FrameworkElement);
        ShowNotification("NotifyReset", "info");
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
        PaneGif.Visibility     = key == "gif"     ? Visibility.Visible : Visibility.Collapsed;
        PaneHotkeys.Visibility = key == "hotkeys" ? Visibility.Visible : Visibility.Collapsed;
        PaneInfo.Visibility    = key == "info"    ? Visibility.Visible : Visibility.Collapsed;

        try
        {
            var accent = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ClipsyAccentBrush"];
            var dim = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ClipsyText2Brush"];
            IconNavGeneral.Foreground = key == "general" ? accent : dim;
            IconNavVideo.Foreground   = key == "video"   ? accent : dim;
            IconNavGif.Foreground     = key == "gif"     ? accent : dim;
            IconNavHotkeys.Foreground = key == "hotkeys" ? accent : dim;
            IconNavInfo.Foreground    = key == "info"    ? accent : dim;
        }
        catch { }
    }
}
