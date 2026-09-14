using System;
using Clipsy.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Clipsy.Views.Settings;

public sealed partial class SettingsWindow
{
    private string _eyedropperCustomBinding = "F6";
    private bool _eyedropperListening;

    private void LoadEyedropperModifier()
    {
        var binding = _draft.EyedropperModifier;
        if (IsBuiltInEyedropperModifier(binding))
        {
            SelectComboByTag(EyedropperModBox, binding);
        }
        else if (TryParseEyedropperKey(binding, out _))
        {
            _eyedropperCustomBinding = binding;
            SelectComboByTag(EyedropperModBox, "custom");
        }
        else
        {
            SelectComboByTag(EyedropperModBox, "Alt");
        }
        UpdateEyedropperCustomUi();
    }
    private string GetEyedropperModifierBinding()
    {
        var tag = SelectedComboTag(EyedropperModBox);
        return string.Equals(tag, "custom", StringComparison.OrdinalIgnoreCase)
            ? _eyedropperCustomBinding
            : tag;
    }

    private void OnEyedropperModifierChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateEyedropperCustomUi();
        if (!_loading) MarkChanged();
    }

    private void UpdateEyedropperCustomUi()
    {
        if (EyedropperCustomKeyButton == null || EyedropperModBox == null) return;
        bool custom = string.Equals(SelectedComboTag(EyedropperModBox), "custom", StringComparison.OrdinalIgnoreCase);
        EyedropperCustomKeyButton.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (custom && !_eyedropperListening)
            EyedropperCustomKeyButton.Content = _eyedropperCustomBinding;
    }

    private void OnEyedropperCustomKeyClick(object sender, RoutedEventArgs e)
    {
        if (_eyedropperListening)
        {
            FinishEyedropperListening();
            return;
        }
        if (_listeningButton != null) FinishListening();
        _eyedropperListening = true;
        EyedropperCustomKeyButton.Content = Strings.Get("HkPressKeys");
        Content.KeyDown += OnEyedropperCustomKeyDown;
        Content.Focus(FocusState.Programmatic);
    }
    private void OnEyedropperCustomKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_eyedropperListening) return;
        if (e.Key == VirtualKey.Escape)
        {
            FinishEyedropperListening();
            e.Handled = true;
            return;
        }

        var binding = NormalizeEyedropperKey(e.Key);
        if (string.IsNullOrEmpty(binding))
        {
            e.Handled = true;
            return;
        }

        if (IsBuiltInEyedropperModifier(binding))
        {
            SelectComboByTag(EyedropperModBox, binding);
        }
        else
        {
            _eyedropperCustomBinding = binding;
            SelectComboByTag(EyedropperModBox, "custom");
        }
        FinishEyedropperListening();
        MarkChanged();
        e.Handled = true;
    }

    private void FinishEyedropperListening()
    {
        if (!_eyedropperListening) return;
        _eyedropperListening = false;
        Content.KeyDown -= OnEyedropperCustomKeyDown;
        UpdateEyedropperCustomUi();
    }
    private static bool IsBuiltInEyedropperModifier(string? binding)
        => string.Equals(binding, "Alt", StringComparison.OrdinalIgnoreCase)
        || string.Equals(binding, "Ctrl", StringComparison.OrdinalIgnoreCase)
        || string.Equals(binding, "Shift", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeEyedropperKey(VirtualKey key) => key switch
    {
        VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => "Ctrl",
        VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => "Shift",
        VirtualKey.Menu or VirtualKey.LeftMenu or VirtualKey.RightMenu => "Alt",
        VirtualKey.None => string.Empty,
        _ => key.ToString(),
    };

    private static bool TryParseEyedropperKey(string? binding, out VirtualKey key)
    {
        key = VirtualKey.None;
        if (string.IsNullOrWhiteSpace(binding)) return false;
        if (string.Equals(binding, "Ctrl", StringComparison.OrdinalIgnoreCase)) { key = VirtualKey.Control; return true; }
        if (string.Equals(binding, "Alt", StringComparison.OrdinalIgnoreCase)) { key = VirtualKey.Menu; return true; }
        if (string.Equals(binding, "Shift", StringComparison.OrdinalIgnoreCase)) { key = VirtualKey.Shift; return true; }
        return Enum.TryParse(binding, ignoreCase: true, out key) && key != VirtualKey.None;
    }
}
