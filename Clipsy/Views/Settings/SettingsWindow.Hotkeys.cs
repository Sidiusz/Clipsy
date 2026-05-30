using System.Collections.Generic;
using System.Linq;
using Clipsy.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Clipsy.Views.Settings;

public sealed partial class SettingsWindow
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
            case "mic-toggle":  _draft.HotkeyMicToggle = binding; break;
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
        AddHotkeyRow("mic-toggle",  "HkMicToggle",   _draft.HotkeyMicToggle);
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
}
