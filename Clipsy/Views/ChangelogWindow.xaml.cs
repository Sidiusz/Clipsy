using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Clipsy.Localization;
using Clipsy.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
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
        var localizedBody = ReleaseNotesFormatter.SelectLanguage(body, Strings.Lang);
        stack.Children.Add(BuildMarkdown(localizedBody));

        card.Child = stack;
        return card;
    }

    private FrameworkElement BuildMarkdown(string markdown)
    {
        var panel = new StackPanel { Spacing = 5 };
        var lines = (markdown ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        bool pendingGap = false;

        foreach (var raw in lines)
        {
            var text = raw.Trim();
            if (text.Length == 0) { pendingGap = panel.Children.Count > 0; continue; }
            if (pendingGap)
            {
                panel.Children.Add(new Border { Height = 3 });
                pendingGap = false;
            }

            if (IsMarkdownRule(text))
            {
                panel.Children.Add(new Border
                {
                    Style = (Style)Application.Current.Resources["ClipsyHairline"],
                    Margin = new Thickness(0, 4, 0, 4),
                });
                continue;
            }

            int headingLevel = MarkdownHeadingLevel(text);
            if (headingLevel > 0)
            {
                var headingText = text[(headingLevel + 1)..].Trim();
                panel.Children.Add(new TextBlock
                {
                    Text = headingText,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeService.GetBrush("ClipsyTextBrush", RootGrid),
                    FontSize = headingLevel == 1 ? 16 : 14,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Thickness(0, 2, 0, 1),
                });
                continue;
            }

            if (TryStripBullet(text, out var bulletText))
            {
                var row = new Grid { ColumnSpacing = 7 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock
                {
                    Text = "•",
                    Foreground = ThemeService.GetBrush("ClipsyTextBrush", RootGrid),
                    FontSize = 13,
                });
                var content = BuildInlineMarkdown(bulletText);
                Grid.SetColumn(content, 1);
                row.Children.Add(content);
                panel.Children.Add(row);
                continue;
            }

            panel.Children.Add(BuildInlineMarkdown(text));
        }

        return panel;
    }

    private TextBlock BuildInlineMarkdown(string text)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeService.GetBrush("ClipsyTextBrush", RootGrid),
            FontSize = 13,
            IsTextSelectionEnabled = true,
        };

        int position = 0;
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(text, @"\*\*(.+?)\*\*"))
        {
            if (match.Index > position)
                block.Inlines.Add(new Run { Text = text[position..match.Index] });

            var bold = new Bold();
            bold.Inlines.Add(new Run { Text = match.Groups[1].Value });
            block.Inlines.Add(bold);
            position = match.Index + match.Length;
        }

        if (position < text.Length)
            block.Inlines.Add(new Run { Text = text[position..] });
        if (block.Inlines.Count == 0)
            block.Inlines.Add(new Run { Text = text });
        return block;
    }

    private static bool IsMarkdownRule(string text)
    {
        if (text.Length < 3) return false;
        char c = text[0];
        if (c is not ('-' or '_' or '*')) return false;
        foreach (char ch in text)
            if (ch != c) return false;
        return true;
    }

    private static int MarkdownHeadingLevel(string text)
    {
        int level = 0;
        while (level < text.Length && level < 6 && text[level] == '#') level++;
        return level > 0 && level < text.Length && text[level] == ' ' ? level : 0;
    }

    private static bool TryStripBullet(string text, out string content)
    {
        if (text.Length >= 2 && text[1] == ' ' && text[0] is '-' or '*' or '+')
        {
            content = text[2..].TrimStart();
            return true;
        }
        content = string.Empty;
        return false;
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
