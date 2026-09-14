using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Clipsy.Localization;
using Clipsy.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Clipsy.Views;

public sealed partial class ChangelogWindow : Window
{
    private static ChangelogWindow? _open;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private bool _revealed;
    private bool _closed;
    private int _revealFrames;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _revealFallback;

    public ChangelogWindow()
    {
        InitializeComponent();
        ThemeService.Register(Content as FrameworkElement);
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        SetCloak(true);
        _appWindow.Title = Strings.Get("ChangelogTitle");
        _appWindow.Resize(new SizeInt32(560, 680));

        TitleLabel.Text = Strings.Get("ChangelogTitle");
        HeaderLabel.Text = Strings.Get("ChangelogLoading");

        Closed += (_, _) =>
        {
            _closed = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRevealFrame;
            _revealFallback?.Stop();
            if (_open == this) _open = null;
        };
        if (Content is FrameworkElement fe) fe.Loaded += (_, _) => _ = LoadAsync();
    }

    public static void ShowWindow()
    {
        if (_open != null) { _open.Activate(); return; }
        var w = new ChangelogWindow();
        _open = w;
        w.CenterOnScreen();
        w.ArmReveal();
        w.Activate();
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(
            Win32Interop.GetWindowIdFromWindow(_hwnd), DisplayAreaFallback.Primary).WorkArea;
        _appWindow.Move(new PointInt32(
            area.X + (area.Width - 560) / 2, area.Y + (area.Height - 680) / 2));
    }

    private void ArmReveal()
    {
        _revealed = false;
        _revealFrames = 0;
        SetCloak(true);
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRevealFrame;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRevealFrame;
        _revealFallback?.Stop();
        _revealFallback = DispatcherQueue.CreateTimer();
        _revealFallback.Interval = TimeSpan.FromMilliseconds(450);
        _revealFallback.IsRepeating = false;
        _revealFallback.Tick += (_, _) => CompleteReveal();
        _revealFallback.Start();
    }

    private void OnRevealFrame(object? sender, object e)
    {
        if (_revealed) return;
        if (++_revealFrames >= 2) CompleteReveal();
    }

    private void CompleteReveal()
    {
        if (_revealed) return;
        _revealed = true;
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRevealFrame;
        _revealFallback?.Stop();
        SetCloak(false);
    }

    private void SetCloak(bool on)
    {
        try
        {
            int value = on ? 1 : 0;
            DwmSetWindowAttribute(_hwnd, DWMWA_CLOAK, ref value, sizeof(int));
        }
        catch { }
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        var current = UpdateService.CurrentVersion();
        var releases = await UpdateService.FetchReleasesAsync();
        if (_closed) return;

        if (releases.Count == 0)
        {
            HeaderLabel.Text = Strings.Get("ChangelogEmptyOrFail");
            return;
        }

        var newest = releases[0].Version;
        HeaderLabel.Text = string.Format(Strings.Get("ChangelogHeader"), current, newest);

        ThemeService.ApplyTo(Content as FrameworkElement);
        for (int i = 0; i < releases.Count; i++)
            ReleaseList.Children.Add(BuildCard(releases[i], releases[i].Version == current, i == 0));
    }

    private Border BuildCard(ReleaseNote r, bool isCurrent, bool isNewest)
    {
        var accent = ThemeService.GetBrush("ClipsyAccentBrush", RootGrid);
        var card = new Border
        {
            Style = (Style)Application.Current.Resources["ClipsyGroupCard"],
            BorderBrush = isCurrent ? accent : ThemeService.GetBrush("ClipsyBorderBrush", RootGrid),
            BorderThickness = new Thickness(isCurrent ? 1.5 : 1),
        };

        var stack = new StackPanel { Spacing = 8 };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock
        {
            Text = "v" + r.Version,
            Style = (Style)Application.Current.Resources["ClipsyBodyStrong"],
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (isCurrent) head.Children.Add(Chip(Strings.Get("ChangelogCurrent"), accent, Colors.Black));
        else if (isNewest) head.Children.Add(Chip(Strings.Get("ChangelogNew"),
            new SolidColorBrush(Color.FromArgb(0xFF, 0x3F, 0xB9, 0x50)), Colors.White));
        if (r.Published != DateTime.MinValue)
            head.Children.Add(new TextBlock
            {
                Text = r.Published.ToLocalTime().ToString("yyyy-MM-dd"),
                Style = (Style)Application.Current.Resources["ClipsyHelper"],
                VerticalAlignment = VerticalAlignment.Center,
            });
        stack.Children.Add(head);

        var body = string.IsNullOrWhiteSpace(r.Notes) ? r.Title : r.Notes;
        stack.Children.Add(new TextBlock
        {
            Text = (body ?? "").Trim(),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeService.GetBrush("ClipsyTextBrush", RootGrid),
            FontSize = 13,
            IsTextSelectionEnabled = true,
        });

        card.Child = stack;
        return card;
    }

    private static Border Chip(string text, Brush background, Color fg)
        => new()
        {
            Background = background,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 1, 7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(fg),
            },
        };

    private const int DWMWA_CLOAK = 13;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

}
