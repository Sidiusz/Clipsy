using System;
using Clipsy.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Clipsy.Views;

/// <summary>Shared screenshot/recording color picker.</summary>
public sealed partial class ColorPickerPanel : UserControl
{
    public event Action<Color>? ColorPreviewChanged;
    public event Action<Color>? ColorConfirmed;
    public event Action? ColorCanceled;
    public event Action? EyedropperRequested;

    private bool _syncing;
    private bool _previewQueued;
    private Color _currentColor = Microsoft.UI.Colors.Red;
    private double _hue;
    private double _saturation = 1.0;
    private double _value = 1.0;

    public ColorPickerPanel()
    {
        InitializeComponent();
        ValueSlider.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnValueSliderPointerReleased), true);
        Unloaded += OnUnloaded;
        SetColor(Microsoft.UI.Colors.Red);
        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        ToolTipService.SetToolTip(EyedropperBtn, Strings.Get("TipEyedropper"));
        ToolTipService.SetToolTip(CancelBtn, Strings.Get("TipColorCancel"));
        ToolTipService.SetToolTip(ConfirmBtn, Strings.Get("TipColorApply"));
    }

    public Color Color
    {
        get => _currentColor;
        set => SetColor(value);
    }

    public bool ShowEyedropper
    {
        get => EyedropperBtn.Visibility == Visibility.Visible;
        set => EyedropperBtn.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetColor(Color color)
    {
        var opaque = Opaque(color);
        var hsv = RgbToHsv(opaque);
        _currentColor = opaque;
        _hue = hsv.H;
        _saturation = hsv.S;
        _value = hsv.V;
        _syncing = true;
        try
        {
            ColorPickerCtl.Color = opaque;
            ValueSlider.Value = hsv.V * 100.0;
            UpdateValueGradient(hsv.H, hsv.S);
        }
        finally { _syncing = false; }
    }

    private void OnColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncing) return;
        _currentColor = Opaque(args.NewColor);
        var hsv = RgbToHsv(_currentColor);
        _hue = hsv.H;
        _saturation = hsv.S;
        _value = hsv.V;
        _syncing = true;
        try { ValueSlider.Value = hsv.V * 100.0; }
        finally { _syncing = false; }
        UpdateValueGradient(hsv.H, hsv.S);
        QueuePreview();
    }
    private void OnValueSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        _value = Math.Clamp(e.NewValue / 100.0, 0, 1);
        _currentColor = HsvToRgb(_hue, _saturation, _value);
        QueuePreview();
    }

    private void OnValueSliderPointerReleased(object sender, PointerRoutedEventArgs e)
        => SyncPickerColor();

    private void OnValueSliderKeyUp(object sender, KeyRoutedEventArgs e)
        => SyncPickerColor();

    private void OnValueSliderLostFocus(object sender, RoutedEventArgs e)
        => SyncPickerColor();

    private void SyncPickerColor()
    {
        var pickerColor = Opaque(ColorPickerCtl.Color);
        if (pickerColor.R == _currentColor.R && pickerColor.G == _currentColor.G &&
            pickerColor.B == _currentColor.B)
            return;

        _syncing = true;
        try { ColorPickerCtl.Color = _currentColor; }
        finally { _syncing = false; }
    }

    private void QueuePreview()
    {
        if (_previewQueued) return;
        _previewQueued = true;
        CompositionTarget.Rendering += OnPreviewRendering;
    }

    private void OnPreviewRendering(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnPreviewRendering;
        _previewQueued = false;
        ColorPreviewChanged?.Invoke(_currentColor);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_previewQueued) return;
        CompositionTarget.Rendering -= OnPreviewRendering;
        _previewQueued = false;
    }

    private void UpdateValueGradient(double hue, double saturation)
        => ValueGradientStop.Color = HsvToRgb(hue, saturation, 1.0);

    private void OnConfirmClick(object sender, RoutedEventArgs e)
        => ColorConfirmed?.Invoke(_currentColor);

    private void OnCancelClick(object sender, RoutedEventArgs e)
        => ColorCanceled?.Invoke();

    private void OnEyedropperClick(object sender, RoutedEventArgs e)
        => EyedropperRequested?.Invoke();

    private static Color Opaque(Color c) => Color.FromArgb(0xFF, c.R, c.G, c.B);

    private readonly record struct Hsv(double H, double S, double V);
    private static Hsv RgbToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double h = 0;
        if (delta > 0)
        {
            if (max == r) h = 60.0 * (((g - b) / delta) % 6.0);
            else if (max == g) h = 60.0 * (((b - r) / delta) + 2.0);
            else h = 60.0 * (((r - g) / delta) + 4.0);
            if (h < 0) h += 360.0;
        }
        return new Hsv(h, max <= 0 ? 0 : delta / max, max);
    }

    private static Color HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        double chroma = v * s;
        double x = chroma * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = v - chroma;
        (double r, double g, double b) = h switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0.0, chroma, x),
            < 240 => (0.0, x, chroma),
            < 300 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        return Color.FromArgb(0xFF,
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
