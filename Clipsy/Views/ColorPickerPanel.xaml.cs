using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Clipsy.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace Clipsy.Views;

/// <summary>Shared lightweight HSV picker for screenshot and recording tools.</summary>
public sealed partial class ColorPickerPanel : UserControl
{
    private const int SpectrumPixelWidth = 312;
    private const int SpectrumPixelHeight = 196;

    public event Action<Color>? ColorPreviewChanged;
    public event Action<Color>? ColorConfirmed;
    public event Action? ColorCanceled;
    public event Action? EyedropperRequested;

    private bool _syncing;
    private bool _previewQueued;
    private bool _spectrumDragging;
    private bool _spectrumFrameQueued;
    private Windows.Foundation.Point _pendingSpectrumPoint;
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
        CreateSpectrumBitmap();
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
        try { ValueSlider.Value = hsv.V * 100.0; }
        finally { _syncing = false; }

        UpdateValueGradient();
        UpdateSpectrumIndicator();
        UpdateHexText();
    }

    private void CreateSpectrumBitmap()
    {
        var bitmap = new WriteableBitmap(SpectrumPixelWidth, SpectrumPixelHeight);
        var pixels = new byte[SpectrumPixelWidth * SpectrumPixelHeight * 4];
        int i = 0;
        for (int y = 0; y < SpectrumPixelHeight; y++)
        {
            double saturation = y / (double)(SpectrumPixelHeight - 1);
            for (int x = 0; x < SpectrumPixelWidth; x++)
            {
                double hue = x / (double)(SpectrumPixelWidth - 1) * 359.999;
                var color = HsvToRgb(hue, saturation, 1.0);
                pixels[i++] = color.B;
                pixels[i++] = color.G;
                pixels[i++] = color.R;
                pixels[i++] = 0xFF;
            }
        }

        using var stream = bitmap.PixelBuffer.AsStream();
        stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        SpectrumImage.Source = bitmap;
    }

    private void OnSpectrumPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _spectrumDragging = true;
        SpectrumSurface.CapturePointer(e.Pointer);
        UpdateSpectrumFromPoint(e.GetCurrentPoint(SpectrumSurface).Position);
        e.Handled = true;
    }
    private void OnSpectrumPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_spectrumDragging) return;
        QueueSpectrumUpdate(e.GetCurrentPoint(SpectrumSurface).Position);
        e.Handled = true;
    }

    private void OnSpectrumPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_spectrumDragging) return;
        CancelSpectrumFrame();
        UpdateSpectrumFromPoint(e.GetCurrentPoint(SpectrumSurface).Position);
        _spectrumDragging = false;
        SpectrumSurface.ReleasePointerCapture(e.Pointer);
        UpdateHexText();
        e.Handled = true;
    }

    private void OnSpectrumPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_spectrumDragging) return;
        if (_spectrumFrameQueued)
        {
            var pending = _pendingSpectrumPoint;
            CancelSpectrumFrame();
            UpdateSpectrumFromPoint(pending);
        }
        _spectrumDragging = false;
        UpdateHexText();
    }

    private void QueueSpectrumUpdate(Windows.Foundation.Point point)
    {
        _pendingSpectrumPoint = point;
        if (_spectrumFrameQueued) return;
        _spectrumFrameQueued = true;
        CompositionTarget.Rendering += OnSpectrumRendering;
    }

    private void OnSpectrumRendering(object? sender, object e)
    {
        CompositionTarget.Rendering -= OnSpectrumRendering;
        _spectrumFrameQueued = false;
        UpdateSpectrumFromPoint(_pendingSpectrumPoint);
    }

    private void CancelSpectrumFrame()
    {
        if (!_spectrumFrameQueued) return;
        CompositionTarget.Rendering -= OnSpectrumRendering;
        _spectrumFrameQueued = false;
    }

    private void UpdateSpectrumFromPoint(Windows.Foundation.Point point)
    {
        double width = Math.Max(2.0, SpectrumSurface.ActualWidth);
        double height = Math.Max(2.0, SpectrumSurface.ActualHeight);
        _hue = Math.Clamp(point.X / (width - 1.0), 0, 1) * 359.999;
        _saturation = Math.Clamp(point.Y / (height - 1.0), 0, 1);
        _currentColor = HsvToRgb(_hue, _saturation, _value);
        UpdateSpectrumIndicator();
        UpdateValueGradient();
        QueuePreview();
    }

    private void UpdateSpectrumIndicator()
    {
        double width = SpectrumSurface.ActualWidth > 1
            ? SpectrumSurface.ActualWidth : SpectrumPixelWidth;
        double height = SpectrumSurface.ActualHeight > 1
            ? SpectrumSurface.ActualHeight : SpectrumPixelHeight;
        double x = _hue / 359.999 * (width - 1.0);
        double y = _saturation * (height - 1.0);
        Canvas.SetLeft(SpectrumIndicator, x - SpectrumIndicator.Width / 2.0);
        Canvas.SetTop(SpectrumIndicator, y - SpectrumIndicator.Height / 2.0);
    }

    private void OnValueSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        _value = Math.Clamp(e.NewValue / 100.0, 0, 1);
        _currentColor = HsvToRgb(_hue, _saturation, _value);
        QueuePreview();
    }

    private void OnValueSliderPointerReleased(object sender, PointerRoutedEventArgs e)
        => UpdateHexText();
    private void OnValueSliderKeyUp(object sender, KeyRoutedEventArgs e)
        => UpdateHexText();

    private void OnValueSliderLostFocus(object sender, RoutedEventArgs e)
        => UpdateHexText();

    private void OnHexKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        ApplyHexText();
        e.Handled = true;
    }

    private void OnHexLostFocus(object sender, RoutedEventArgs e)
        => ApplyHexText();

    private void ApplyHexText()
    {
        if (!TryParseHex(HexBox.Text, out var color))
        {
            UpdateHexText();
            return;
        }
        SetColor(color);
        QueuePreview();
    }

    private void UpdateHexText()
        => HexBox.Text = $"#{_currentColor.R:X2}{_currentColor.G:X2}{_currentColor.B:X2}";
    private static bool TryParseHex(string? text, out Color color)
    {
        color = Microsoft.UI.Colors.Red;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string value = text.Trim().TrimStart('#');
        if (value.Length == 8) value = value[2..];
        if (value.Length != 6) return false;

        if (!byte.TryParse(value.AsSpan(0, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out byte r) ||
            !byte.TryParse(value.AsSpan(2, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out byte g) ||
            !byte.TryParse(value.AsSpan(4, 2), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out byte b))
            return false;

        color = Color.FromArgb(0xFF, r, g, b);
        return true;
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
        CancelSpectrumFrame();
        if (!_previewQueued) return;
        CompositionTarget.Rendering -= OnPreviewRendering;
        _previewQueued = false;
    }

    private void UpdateValueGradient()
        => ValueGradientStop.Color = HsvToRgb(_hue, _saturation, 1.0);

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        ApplyHexText();
        ColorConfirmed?.Invoke(_currentColor);
    }

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
