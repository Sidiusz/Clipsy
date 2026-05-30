using System;
using System.Diagnostics;
using System.Threading;
using Clipsy.Localization;
using Clipsy.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Clipsy.Views.Settings;

public sealed partial class SettingsWindow
{
    private void OnResolutionSegmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton clicked) return;
        foreach (var btn in new[] { ResBtn480p, ResBtn720p, ResBtn1080p, ResBtn1440p, ResBtnOriginal })
            btn.IsChecked = btn == clicked;
        UpdateBitrateBounds(clicked.Tag as string ?? string.Empty);
        UpdateBitrateLabel();
        MarkChanged();
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

        UpdateVideoFormatAvailability(ffmpegAvailable);
    }

    /// <summary>
    /// AVI/MKV containers need FFmpeg for a correct remux. Without it, disable
    /// those choices and snap the selection back to MP4. MP4 and GIF stay
    /// available (both work natively).
    /// </summary>
    private void UpdateVideoFormatAvailability(bool ffmpegAvailable)
    {
        if (VidFmtAvi == null || VidFmtMkv == null) return;
        VidFmtAvi.IsEnabled = ffmpegAvailable;
        VidFmtMkv.IsEnabled = ffmpegAvailable;

        if (!ffmpegAvailable)
        {
            var fmt = SelectedComboTag(VideoFormatBox);
            if (fmt == "avi" || fmt == "mkv")
            {
                var wasLoading = _loading;
                _loading = true;
                SelectComboByTag(VideoFormatBox, "mp4");
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

    // ============== Bitrate ==============

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

    // ============== Microphone ==============

    private void PopulateMicDevices(string selectedDeviceName)
    {
        MicDeviceBox.SelectionChanged -= OnMicDeviceChanged;
        MicDeviceBox.Items.Clear();

        var defaultItem = new ComboBoxItem { Content = Strings.Get("OptMicDefault"), Tag = "" };
        MicDeviceBox.Items.Add(defaultItem);

        ComboBoxItem? toSelect = defaultItem;
        try
        {
            var devices = ScreenRecorderLib.Recorder.GetSystemAudioDevices(
                ScreenRecorderLib.AudioDeviceSource.InputDevices);
            foreach (var dev in devices)
            {
                var item = new ComboBoxItem { Content = dev.FriendlyName, Tag = dev.DeviceName };
                MicDeviceBox.Items.Add(item);
                if (dev.DeviceName == selectedDeviceName) toSelect = item;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Clipsy] PopulateMicDevices failed: {ex.Message}");
        }

        MicDeviceBox.SelectedItem = toSelect;
        MicDeviceBox.SelectionChanged += OnMicDeviceChanged;
    }

    private void UpdateMicDevicePanelVisibility()
    {
        if (MicDevicePanel == null) return;
        MicDevicePanel.Visibility = (MicEnabledSwitch?.IsChecked == true)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMicEnabledToggled(object sender, RoutedEventArgs e)
    {
        UpdateMicDevicePanelVisibility();
        if (!_loading) MarkChanged();
    }

    private void OnMicDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) MarkChanged();
    }

    // ============== GIF ==============

    private void OnGifColorChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (GifColorLabel != null) GifColorLabel.Text = ((int)GifColorSlider.Value).ToString();
    }

    private void OnGifFpsChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (GifFpsLabel != null) GifFpsLabel.Text = ((int)GifFpsSlider.Value).ToString();
    }
}
