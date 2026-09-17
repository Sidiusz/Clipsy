using System;
using System.Text;
using System.Text.RegularExpressions;
using Clipsy.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace Clipsy.Views;

public sealed partial class ChangelogWindow
{
    private static readonly Regex UnorderedListPattern = new(
        @"^\s*[-+*]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex OrderedListPattern = new(
        @"^\s*(\d+)[.)]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex TaskPattern = new(
        @"^\[( |x|X)\]\s+(.+)$", RegexOptions.Compiled);
    private static readonly Regex RulePattern = new(
        @"^\s*(?:(?:\*\s*){3,}|(?:-\s*){3,}|(?:_\s*){3,})$", RegexOptions.Compiled);
    private static readonly Regex InlinePattern = new(
        @"(?<code>`(?<codeText>[^`\n]+)`)" +
        @"|(?<link>(?<!!)\[(?<linkText>[^\]]+)\]\((?<url>https?://[^)\s]+)\))" +
        @"|(?<bold>\*\*(?<boldText>.+?)\*\*|__(?<boldText2>.+?)__)" +
        @"|(?<italic>(?<!\*)\*(?<italicText>[^*\n]+)\*(?!\*)|(?<!_)_(?<italicText2>[^_\n]+)_(?!_))",
        RegexOptions.Compiled);

    private FrameworkElement BuildMarkdown(string markdown)
    {
        var panel = new StackPanel { Spacing = 6 };
        var lines = (markdown ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        for (int i = 0; i < lines.Length;)
        {
            string text = lines[i].Trim();
            if (text.Length == 0) { i++; continue; }

            if (TryFence(text, out var fence))
            {
                i = AddCodeBlock(panel, lines, i, fence);
                continue;
            }
            if (RulePattern.IsMatch(text))
            {
                panel.Children.Add(new Border
                {
                    Style = (Style)Application.Current.Resources["ClipsyHairline"],
                    Margin = new Thickness(0, 4, 0, 4),
                });
                i++;
                continue;
            }

            int headingLevel = MarkdownHeadingLevel(text);
            if (headingLevel > 0)
            {
                string headingText = text[(headingLevel + 1)..].Trim();
                var heading = BuildInlineMarkdown(headingText,
                    headingLevel == 1 ? 16 : headingLevel == 2 ? 15 : 14);
                heading.FontWeight = FontWeights.SemiBold;
                heading.Margin = new Thickness(0, 2, 0, 1);
                panel.Children.Add(heading);
                i++;
                continue;
            }
            if (text.StartsWith('>'))
            {
                var quote = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith('>'))
                {
                    string quoted = lines[i].TrimStart()[1..].TrimStart();
                    if (quote.Length > 0) quote.AppendLine();
                    quote.Append(quoted);
                    i++;
                }
                panel.Children.Add(new Border
                {
                    BorderBrush = ThemeService.GetBrush("ClipsyAccentBrush", RootGrid),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(10, 2, 0, 2),
                    Child = BuildMarkdown(quote.ToString()),
                });
                continue;
            }

            var unordered = UnorderedListPattern.Match(lines[i]);
            if (unordered.Success)
            {
                AddListRow(panel, "•", unordered.Groups[1].Value.Trim());
                i++;
                continue;
            }
            var ordered = OrderedListPattern.Match(lines[i]);
            if (ordered.Success)
            {
                AddListRow(panel, ordered.Groups[1].Value + ".", ordered.Groups[2].Value.Trim());
                i++;
                continue;
            }

            var paragraph = new StringBuilder();
            while (i < lines.Length)
            {
                string candidate = lines[i].Trim();
                if (candidate.Length == 0 || IsBlockStart(candidate)) break;
                if (paragraph.Length > 0) paragraph.Append(' ');
                paragraph.Append(candidate);
                i++;
            }
            panel.Children.Add(BuildInlineMarkdown(paragraph.ToString()));
            if (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
        }

        return panel;
    }

    private int AddCodeBlock(StackPanel panel, string[] lines, int index, string fence)
    {
        var code = new StringBuilder();
        index++;
        while (index < lines.Length && !lines[index].TrimStart().StartsWith(fence, StringComparison.Ordinal))
        {
            if (code.Length > 0) code.AppendLine();
            code.Append(lines[index]);
            index++;
        }
        if (index < lines.Length) index++;

        panel.Children.Add(new Border
        {
            Background = ThemeService.GetBrush("ClipsyBg2Brush", RootGrid),
            BorderBrush = ThemeService.GetBrush("ClipsyBorderSubtleBrush", RootGrid),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new TextBlock
            {
                Text = code.ToString(),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontFamily = MarkdownMonoFont(),
                FontSize = 12,
                Foreground = ThemeService.GetBrush("ClipsyTextBrush", RootGrid),
            },
        });
        return index;
    }
    private void AddListRow(StackPanel panel, string marker, string content)
    {
        var task = TaskPattern.Match(content);
        if (task.Success)
        {
            marker = string.Equals(task.Groups[1].Value, " ", StringComparison.Ordinal) ? "☐" : "☑";
            content = task.Groups[2].Value;
        }

        var row = new Grid { ColumnSpacing = 7 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = marker,
            Foreground = ThemeService.GetBrush("ClipsyText2Brush", RootGrid),
            FontSize = 13,
            MinWidth = 14,
        });
        var body = BuildInlineMarkdown(content);
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        panel.Children.Add(row);
    }

    private TextBlock BuildInlineMarkdown(string text, double fontSize = 13)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeService.GetBrush("ClipsyTextBrush", RootGrid),
            FontSize = fontSize,
            IsTextSelectionEnabled = true,
        };

        int position = 0;
        foreach (Match match in InlinePattern.Matches(text))
        {
            if (match.Index > position)
                block.Inlines.Add(new Run { Text = text[position..match.Index] });

            if (match.Groups["code"].Success)
            {
                block.Inlines.Add(new Run
                {
                    Text = match.Groups["codeText"].Value,
                    FontFamily = MarkdownMonoFont(),
                    Foreground = ThemeService.GetBrush("ClipsyAccentBrush", RootGrid),
                });
            }
            else if (match.Groups["link"].Success)
            {
                AddLink(block, match.Groups["linkText"].Value, match.Groups["url"].Value);
            }
            else if (match.Groups["bold"].Success)
            {
                string value = match.Groups["boldText"].Success
                    ? match.Groups["boldText"].Value
                    : match.Groups["boldText2"].Value;
                var bold = new Bold();
                bold.Inlines.Add(new Run { Text = value });
                block.Inlines.Add(bold);
            }
            else if (match.Groups["italic"].Success)
            {
                string value = match.Groups["italicText"].Success
                    ? match.Groups["italicText"].Value
                    : match.Groups["italicText2"].Value;
                var italic = new Italic();
                italic.Inlines.Add(new Run { Text = value });
                block.Inlines.Add(italic);
            }

            position = match.Index + match.Length;
        }

        if (position < text.Length)
            block.Inlines.Add(new Run { Text = text[position..] });
        if (block.Inlines.Count == 0)
            block.Inlines.Add(new Run { Text = text });
        return block;
    }
    private void AddLink(TextBlock block, string label, string rawUrl)
    {
        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var link = new Hyperlink { NavigateUri = uri };
            link.Inlines.Add(new Run { Text = label });
            block.Inlines.Add(link);
            return;
        }

        block.Inlines.Add(new Run { Text = label });
    }

    private static bool IsBlockStart(string text)
        => TryFence(text, out _) ||
           RulePattern.IsMatch(text) ||
           MarkdownHeadingLevel(text) > 0 ||
           text.StartsWith('>') ||
           UnorderedListPattern.IsMatch(text) ||
           OrderedListPattern.IsMatch(text);

    private static bool TryFence(string text, out string fence)
    {
        string trimmed = text.TrimStart();
        if (trimmed.StartsWith("```", StringComparison.Ordinal)) { fence = "```"; return true; }
        if (trimmed.StartsWith("~~~", StringComparison.Ordinal)) { fence = "~~~"; return true; }
        fence = string.Empty;
        return false;
    }
    private static int MarkdownHeadingLevel(string text)
    {
        int level = 0;
        while (level < text.Length && level < 6 && text[level] == '#') level++;
        return level > 0 && level < text.Length && text[level] == ' ' ? level : 0;
    }

    private static FontFamily MarkdownMonoFont()
    {
        if (Application.Current.Resources.TryGetValue("ClipsyFontFamilyMono", out var value) &&
            value is string family && !string.IsNullOrWhiteSpace(family))
            return new FontFamily(family);

        return new FontFamily("Cascadia Mono, Consolas");
    }
}
