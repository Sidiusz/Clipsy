using System.Collections.Generic;
using System.Linq;
using Clipsy.Localization;
using Clipsy.Services;
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
        public string GroupLabel { get; init; } = string.Empty;
        public Visibility GroupVisibility { get; init; } = Visibility.Collapsed;
        // First group sits flush under the column header, which already draws the line above it.
        public Thickness GroupMargin { get; init; } = new(0, 8, 0, 2);
        public Thickness GroupBorder { get; init; } = new(0, 1, 0, 1);
        // The next group's border already separates the last row of a group.
        public Visibility SeparatorVisibility { get; init; } = Visibility.Visible;
        // Global rows are registered system-wide and follow the stricter key rules.
        public bool IsGlobal { get; init; }
        public Visibility ClearVisibility { get; init; } = Visibility.Collapsed;
        public string ClearTip { get; init; } = string.Empty;
        public string DefaultTip { get; init; } = string.Empty;
        public string DefaultBinding { get; init; } = string.Empty;

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
            set
            {
                if (_binding == value) return;
                _binding = value;
                OnChanged();
                OnChanged(nameof(Display));
                OnChanged(nameof(DefaultVisibility));
            }
        }

        public string Display => _binding.Length == 0 ? Strings.Get("HkNotSet") : _binding;

        public Visibility DefaultVisibility =>
            string.Equals(_binding, DefaultBinding, System.StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed : Visibility.Visible;

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
            case "select-monitor": _draft.HotkeySelectMonitor = binding; break;
            case "record-save": _draft.HotkeyRecordSilentSave = binding; break;
            case "mic-toggle":  _draft.HotkeyMicToggle = binding; break;
        }
    }

    private static string GetHotkey(AppSettings s, string key) => key switch
    {
        "capture" => s.HotkeyCapture,
        "save-silent" => s.HotkeyScreenshotSilent,
        "copy" => s.HotkeyCopy,
        "undo" => s.HotkeyUndo,
        "redo" => s.HotkeyRedo,
        "select-all" => s.HotkeySelectAll,
        "select-monitor" => s.HotkeySelectMonitor,
        "record-save" => s.HotkeyRecordSilentSave,
        "mic-toggle" => s.HotkeyMicToggle,
        _ => string.Empty,
    };

    // Registered system-wide (the rest only work inside the capture overlay).
    private static bool IsGlobalHotkey(string key) => key is "capture" or "record-save" or "mic-toggle";

    private void BuildHotkeyRows()
    {
        foreach (var existing in _hotkeyRows) existing.PropertyChanged -= OnHotkeyRowChanged;
        _hotkeyRows.Clear();
        var defaults = new AppSettings();
        var specs = new (string Key, string LabelKey, string Binding, string? GroupKey)[]
        {
            ("capture", "HkOpenCapture", _draft.HotkeyCapture, "HkGroupGeneral"),
            ("undo", "HkUndo", _draft.HotkeyUndo, "HkGroupEditing"),
            ("redo", "HkRedo", _draft.HotkeyRedo, null),
            ("save-silent", "HkSaveSilent", _draft.HotkeyScreenshotSilent, "HkGroupScreenshot"),
            ("copy", "HkCopy", _draft.HotkeyCopy, null),
            ("select-all", "HkSelectAll", _draft.HotkeySelectAll, null),
            ("select-monitor", "HkSelectMonitor", _draft.HotkeySelectMonitor, null),
            ("record-save", "HkRecordSave", _draft.HotkeyRecordSilentSave, "HkGroupVideo"),
            ("mic-toggle", "HkMicToggle", _draft.HotkeyMicToggle, null),
        };
        for (int i = 0; i < specs.Length; i++)
        {
            var s = specs[i];
            bool lastInGroup = i == specs.Length - 1 || specs[i + 1].GroupKey != null;
            var row = new HotkeyRow
            {
                Key = s.Key,
                Label = Strings.Get(s.LabelKey),
                DefaultBinding = GetHotkey(defaults, s.Key),
                Binding = s.Binding,
                GroupLabel = s.GroupKey == null ? string.Empty : Strings.Get(s.GroupKey),
                GroupVisibility = s.GroupKey == null ? Visibility.Collapsed : Visibility.Visible,
                GroupMargin = i == 0 ? new Thickness(0, 0, 0, 2) : new Thickness(0, 8, 0, 2),
                GroupBorder = i == 0 ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 1),
                SeparatorVisibility = lastInGroup ? Visibility.Collapsed : Visibility.Visible,
                IsGlobal = IsGlobalHotkey(s.Key),
                // Only the optional recording hotkeys can be left unset.
                ClearVisibility = s.Key is "record-save" or "mic-toggle" ? Visibility.Visible : Visibility.Collapsed,
                ClearTip = Strings.Get("HkClear"),
                DefaultTip = Strings.Get("HkDefault"),
            };
            row.PropertyChanged += OnHotkeyRowChanged;
            _hotkeyRows.Add(row);
        }
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
        FinishEyedropperListening();
        _listeningButton = b;
        _listeningKey = key;
        _listeningContent = b.Content;
        b.Content = Strings.Get("HkPressKeys");
        try
        {
            b.Background = ThemeService.GetBrush("ClipsyBg2Brush", Content as FrameworkElement);
            b.BorderBrush = ThemeService.GetBrush("ClipsyAccentBrush", Content as FrameworkElement);
        }
        catch { }
        Content.KeyDown -= OnRebindKeyDown;
        Content.KeyDown += OnRebindKeyDown;
        Content.KeyUp -= OnRebindKeyUp;
        Content.KeyUp += OnRebindKeyUp;
    }

    private object? _listeningContent;

    private void OnRebindKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_listeningButton == null || _listeningKey == null) return;
        e.Handled = true;
        if (e.Key == VirtualKey.Escape)
        {
            FinishListening();
            return;
        }
        // Win alone: its combos are usually taken by the shell before they get here.
        if (e.Key is VirtualKey.LeftWindows or VirtualKey.RightWindows)
        {
            ShowNotification("HkReservedKey", "warning");
            return;
        }
        if (IsModifierOnly(e.Key)) return;
        TryAssignChord(e.Key);
    }

    // Windows sends no KeyDown for PrintScreen, only KeyUp.
    private void OnRebindKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (_listeningButton == null || _listeningKey == null || e.Key != VirtualKey.Snapshot) return;
        e.Handled = true;
        TryAssignChord(e.Key);
    }

    private void TryAssignChord(VirtualKey key)
    {
        var row = _hotkeyRows.FirstOrDefault(r => r.Key == _listeningKey);
        if (row == null) return;
        var binding = ChordToString(key);
        string? problem = HotkeyService.CheckBinding(binding, row.IsGlobal) switch
        {
            HotkeyProblem.None => null,
            HotkeyProblem.Reserved => "HkReservedKey",
            HotkeyProblem.NeedsModifier => "HkNeedsModifier",
            _ => "HkUnsupportedKey",
        };
        if (problem != null)
        {
            ShowNotification(problem, "warning");
            return;
        }
        if (TrySetRowBinding(row, binding)) FinishListening();
    }

    private bool TrySetRowBinding(HotkeyRow row, string binding)
    {
        var clash = binding.Length == 0 ? null : _hotkeyRows.FirstOrDefault(r => r.Key != row.Key
            && HotkeyService.SameBinding(r.Binding, binding));
        if (clash != null)
        {
            ShowNotificationText(string.Format(Strings.Get("HkDuplicate"), binding, clash.Label), "warning");
            return false;
        }
        row.Binding = binding;
        return true;
    }

    private void OnHotkeyDefaultClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        var row = _hotkeyRows.FirstOrDefault(r => r.Key == key);
        if (row == null) return;
        if (_listeningButton != null) FinishListening();
        TrySetRowBinding(row, GetHotkey(new AppSettings(), key));
    }

    private void OnHotkeyClearClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        var row = _hotkeyRows.FirstOrDefault(r => r.Key == key);
        if (row == null) return;
        if (_listeningButton != null) FinishListening();
        row.Binding = string.Empty;
    }

    private void FinishListening()
    {
        if (_listeningButton != null)
        {
            // Put back the bound TextBlock so later Default/Clear changes show up.
            if (_listeningContent != null) _listeningButton.Content = _listeningContent;
            try
            {
                _listeningButton.ClearValue(Microsoft.UI.Xaml.Controls.Control.BackgroundProperty);
                _listeningButton.ClearValue(Microsoft.UI.Xaml.Controls.Control.BorderBrushProperty);
            }
            catch { }
        }
        _listeningButton = null;
        _listeningKey = null;
        _listeningContent = null;
        Content.KeyDown -= OnRebindKeyDown;
        Content.KeyUp -= OnRebindKeyUp;
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
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private static bool IsDown(VirtualKey k)
    {
        var s = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(k);
        return (s & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
    }
}
