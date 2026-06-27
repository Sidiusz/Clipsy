using System;
using Clipsy.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace Clipsy.Views;

/// <summary>Shared color-picker panel (spectrum, hex, Cancel/Confirm, optional
/// eyedropper) used by the capture overlay and the recording HUD.</summary>
public sealed partial class ColorPickerPanel : UserControl
{
    /// Fired on every ColorPicker drag — live preview only, don't commit yet.
    public event Action<Color>? ColorPreviewChanged;
    /// Fired when the user clicks Confirm.
    public event Action<Color>? ColorConfirmed;
    /// Fired when the user clicks Cancel.
    public event Action? ColorCanceled;
    /// Fired when the eyedropper button is clicked (only visible when ShowEyedropper=true).
    public event Action? EyedropperRequested;

    public ColorPickerPanel()
    {
        InitializeComponent();
        // Set initial color in code — assigning ColorPicker.Color via XAML markup throws
        // XamlParseException (0x802B000A) on Windows App SDK 1.6 at runtime.
        try { ColorPickerCtl.Color = Microsoft.UI.Colors.Red; } catch { }
        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        ToolTipService.SetToolTip(EyedropperBtn, Strings.Get("TipEyedropper"));
        ToolTipService.SetToolTip(CancelBtn,     Strings.Get("TipColorCancel"));
        ToolTipService.SetToolTip(ConfirmBtn,    Strings.Get("TipColorApply"));
    }

    /// Current color shown in the picker.
    public Color Color
    {
        get => ColorPickerCtl.Color;
        set => ColorPickerCtl.Color = value;
    }

    /// Shows/hides the eyedropper button. Default: hidden (for recording HUD).
    public bool ShowEyedropper
    {
        get => EyedropperBtn.Visibility == Visibility.Visible;
        set => EyedropperBtn.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnColorChanged(ColorPicker sender, ColorChangedEventArgs args)
        => ColorPreviewChanged?.Invoke(args.NewColor);

    private void OnConfirmClick(object sender, RoutedEventArgs e)
        => ColorConfirmed?.Invoke(ColorPickerCtl.Color);

    private void OnCancelClick(object sender, RoutedEventArgs e)
        => ColorCanceled?.Invoke();

    private void OnEyedropperClick(object sender, RoutedEventArgs e)
        => EyedropperRequested?.Invoke();
}
