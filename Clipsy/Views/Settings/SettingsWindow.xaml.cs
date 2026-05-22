using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Clipsy.Views.Settings;

public sealed partial class SettingsWindow : Window
{
    // HotkeyRow + full XAML - does this combination fail?
    public sealed class HotkeyRow : System.ComponentModel.INotifyPropertyChanged
    {
        public string Key { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;

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

    public SettingsWindow()
    {
        InitializeComponent();
        Closed += (_, _) => { if (_current == this) _current = null; };
    }

    public static void ShowOrActivate()
    {
        if (_current != null)
        {
            try { _current.Activate(); } catch { }
            return;
        }
        try
        {
            _current = new SettingsWindow();
            _current.Activate();
        }
        catch (System.Exception ex)
        {
            _current = null;
            Clipsy.Services.Diagnostics.Show("SettingsWindow.Create", ex);
        }
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void OnScreenshotFolderPick(object sender, RoutedEventArgs e) { }
    private void OnVideoFolderPick(object sender, RoutedEventArgs e) { }
    private void OnScreenshotFormatChanged(object sender, SelectionChangedEventArgs e) { }
    private void OnJpgQualityChanged(object sender, RangeBaseValueChangedEventArgs e) { }
    private void OnCodecChanged(object sender, SelectionChangedEventArgs e) { }
    private void OnResolutionChanged(object sender, SelectionChangedEventArgs e) { }
    private void OnBitrateChanged(object sender, RangeBaseValueChangedEventArgs e) { }
    private void OnGifColorChanged(object sender, RangeBaseValueChangedEventArgs e) { }
    private void OnGifFpsChanged(object sender, RangeBaseValueChangedEventArgs e) { }
    private void OnHotkeyRebindClick(object sender, RoutedEventArgs e) { }
    private void OnCheckUpdates(object sender, RoutedEventArgs e) { }
    private void OnReset(object sender, RoutedEventArgs e) { }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnSave(object sender, RoutedEventArgs e) { }
}
