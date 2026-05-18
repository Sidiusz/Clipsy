using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Clipsy.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Clipsy.Views.Settings;

public sealed partial class SettingsWindow : Window
{
    public sealed class HotkeyRow
    {
        public required string Key { get; init; }
        public required string Label { get; init; }
        public string Binding { get; set; } = string.Empty;
    }

    private static SettingsWindow? _current;

    private readonly IntPtr _hwnd;
    private AppSettings _draft;
    private readonly ObservableCollection<HotkeyRow> _hotkeyRows = new();
    private Button? _listeningButton;
    private string? _listeningKey;

    private bool _firstActivated;

    public SettingsWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _draft = SettingsService.Instance.Settings.Clone();
        HotkeyList.ItemsSource = _hotkeyRows;
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
            appWin.Title = "Clipsy Settings";
            appWin.Resize(new SizeInt32(720, 640));
            Load();
            VersionLabel.Text = "Version " + GetVersion();
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
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "0.1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    // ---------- Load / Save ----------

    private void Load()
    {
        SelectComboByTag(LangBox, _draft.Language);
        SelectComboByTag(ThemeBox, _draft.Theme);
        SelectComboByTag(OcrEngineBox, _draft.OcrEngine);
        ScreenshotFolderBox.Text = string.IsNullOrEmpty(_draft.ScreenshotFolder)
            ? SettingsService.Instance.DefaultScreenshotFolder
            : _draft.ScreenshotFolder!;
        VideoFolderBox.Text = string.IsNullOrEmpty(_draft.VideoFolder)
            ? SettingsService.Instance.DefaultVideoFolder
            : _draft.VideoFolder!;
        RememberFolderSwitch.IsOn = _draft.RememberLastFolder;
        SelectComboByTag(ScreenshotFormatBox, _draft.ScreenshotFormat);
        JpgQualitySlider.Value = System.Math.Clamp(_draft.JpgQuality, 50, 100);
        JpgQualityLabel.Text = ((int)JpgQualitySlider.Value).ToString();
        UpdateJpgQualityRowVisibility();
        SelectComboByTag(AfterSaveBox, _draft.AfterSaveAction);
        SelectComboByTag(UpdateIntervalBox, _draft.UpdateInterval);

        SelectComboByTag(CodecBox, _draft.VideoCodec);
        SelectComboByTag(ResolutionBox, _draft.VideoResolution);
        UpdateBitrateBounds(_draft.VideoResolution);
        BitrateSlider.Value = System.Math.Clamp(_draft.VideoBitrateMbps, (int)BitrateSlider.Minimum, (int)BitrateSlider.Maximum);
        UpdateBitrateLabel();

        GifColorSlider.Value = _draft.GifColors;
        GifColorLabel.Text = ((int)GifColorSlider.Value).ToString();
        GifFpsSlider.Value = _draft.GifFps;
        GifFpsLabel.Text = ((int)GifFpsSlider.Value).ToString();
        GifDitherSwitch.IsOn = _draft.GifDither;

        BuildHotkeyRows();
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
        _draft.Theme = SelectedComboTag(ThemeBox);
        _draft.OcrEngine = SelectedComboTag(OcrEngineBox);
        _draft.ScreenshotFolder = ScreenshotFolderBox.Text;
        _draft.VideoFolder = VideoFolderBox.Text;
        _draft.RememberLastFolder = RememberFolderSwitch.IsOn;
        _draft.ScreenshotFormat = SelectedComboTag(ScreenshotFormatBox);
        _draft.JpgQuality = (int)JpgQualitySlider.Value;
        _draft.AfterSaveAction = SelectedComboTag(AfterSaveBox);
        _draft.UpdateInterval = SelectedComboTag(UpdateIntervalBox);

        _draft.VideoCodec = SelectedComboTag(CodecBox);
        _draft.VideoResolution = SelectedComboTag(ResolutionBox);
        _draft.VideoBitrateMbps = (int)BitrateSlider.Value;

        _draft.GifColors = (int)GifColorSlider.Value;
        _draft.GifFps = (int)GifFpsSlider.Value;
        _draft.GifDither = GifDitherSwitch.IsOn;

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
        _hotkeyRows.Clear();
        _hotkeyRows.Add(new HotkeyRow { Key = "capture", Label = "Open capture overlay", Binding = _draft.HotkeyCapture });
        _hotkeyRows.Add(new HotkeyRow { Key = "save-silent", Label = "Save screenshot (silent)", Binding = _draft.HotkeyScreenshotSilent });
        _hotkeyRows.Add(new HotkeyRow { Key = "copy", Label = "Copy to clipboard", Binding = _draft.HotkeyCopy });
        _hotkeyRows.Add(new HotkeyRow { Key = "undo", Label = "Undo", Binding = _draft.HotkeyUndo });
        _hotkeyRows.Add(new HotkeyRow { Key = "redo", Label = "Redo", Binding = _draft.HotkeyRedo });
        _hotkeyRows.Add(new HotkeyRow { Key = "select-all", Label = "Select all", Binding = _draft.HotkeySelectAll });
        _hotkeyRows.Add(new HotkeyRow { Key = "record-save", Label = "Save recording (silent)", Binding = _draft.HotkeyRecordSilentSave });
    }

    // ---------- Folder pickers ----------

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

    // ---------- Video / GIF sliders ----------

    private void OnCodecChanged(object sender, SelectionChangedEventArgs e) { /* codec change has no immediate UI dep */ }

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

    private void OnResolutionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateBitrateBounds(SelectedComboTag(ResolutionBox));
        UpdateBitrateLabel();
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
        if (BitrateSlider.Value > max) BitrateSlider.Value = max;
    }

    private void OnBitrateChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateBitrateLabel();
    }

    private void UpdateBitrateLabel()
    {
        int mbps = (int)BitrateSlider.Value;
        BitrateLabel.Text = mbps + " Mbps";
        // bytes/minute = mbps * 1e6 / 8 * 60
        double mbPerMin = mbps * 60.0 / 8.0;
        long rounded = (long)System.Math.Round(mbPerMin / 10.0) * 10;
        if (rounded == 0) rounded = 10;
        EstFileSizeLabel.Text = $"Est. ~{rounded} MB per minute";
    }

    private void OnGifColorChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        GifColorLabel.Text = ((int)GifColorSlider.Value).ToString();
    }

    private void OnGifFpsChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        GifFpsLabel.Text = ((int)GifFpsSlider.Value).ToString();
    }

    // ---------- Hotkey rebind ----------

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
        b.Content = "Press keys...";
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
            if (row != null && (_listeningButton.Content as string) == "Press keys...")
            {
                _listeningButton.Content = row.Binding;
            }
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

    // ---------- Footer ----------

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            Collect();
            SettingsService.Instance.Replace(_draft);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipsy] Settings save failed: {ex.Message}");
        }
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnReset(object sender, RoutedEventArgs e)
    {
        _draft = new AppSettings();
        Load();
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        try { await Clipsy.App.Current.CheckUpdatesIfDueAsync(force: true); }
        catch (Exception ex) { Debug.WriteLine($"[Clipsy] Forced update check failed: {ex.Message}"); }
    }

    // ---------- Win32 folder picker ----------

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
}
