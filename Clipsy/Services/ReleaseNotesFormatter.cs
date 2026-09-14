using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Clipsy.Services;

public static class ReleaseNotesFormatter
{
    private static readonly Regex LangMarker = new(
        @"<!--\s*lang:(en|ru)\s*-->",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SectionDivider = new(
        @"(?m)^\s*(?:_{3,}|-{3,}|\*{3,})\s*$",
        RegexOptions.Compiled);

    public static string SelectLanguage(string? markdown, string language)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return string.Empty;
        var text = Normalize(markdown);

        var tagged = SelectTagged(text, language);
        if (tagged != null) return tagged;
        var sections = SectionDivider.Split(text)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToArray();

        if (sections.Length <= 1) return StripMarkers(text).Trim();

        bool wantRussian = string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase);
        if (wantRussian)
        {
            var russian = sections
                .Select(s => (Text: s, Cyrillic: CountCyrillic(s)))
                .OrderByDescending(x => x.Cyrillic)
                .First();
            if (russian.Cyrillic > 0) return StripMarkers(russian.Text).Trim();
        }
        else
        {
            var english = sections
                .Select((s, i) => (Text: s, Index: i, Cyrillic: CountCyrillic(s), Latin: CountLatin(s)))
                .OrderBy(x => x.Cyrillic)
                .ThenByDescending(x => x.Latin)
                .ThenBy(x => x.Index)
                .First();
            return StripMarkers(english.Text).Trim();
        }

        return StripMarkers(sections[0]).Trim();
    }
    private static string? SelectTagged(string text, string language)
    {
        var matches = LangMarker.Matches(text);
        if (matches.Count == 0) return null;

        string wanted = string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
        for (int i = 0; i < matches.Count; i++)
        {
            var marker = matches[i];
            if (!string.Equals(marker.Groups[1].Value, wanted, StringComparison.OrdinalIgnoreCase)) continue;

            int start = marker.Index + marker.Length;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            return TrimBoundaryDividers(StripMarkers(text[start..end]));
        }

        return null;
    }

    private static string StripMarkers(string text)
        => LangMarker.Replace(text, string.Empty);

    private static string TrimBoundaryDividers(string text)
    {
        var lines = Normalize(text).Split('\n').ToList();
        while (lines.Count > 0 && (string.IsNullOrWhiteSpace(lines[0]) || SectionDivider.IsMatch(lines[0])))
            lines.RemoveAt(0);
        while (lines.Count > 0 && (string.IsNullOrWhiteSpace(lines[^1]) || SectionDivider.IsMatch(lines[^1])))
            lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines).Trim();
    }

    private static string Normalize(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n');
    private static int CountCyrillic(string text)
    {
        int count = 0;
        foreach (char c in text)
            if (c is >= '\u0400' and <= '\u04FF') count++;
        return count;
    }

    private static int CountLatin(string text)
    {
        int count = 0;
        foreach (char c in text)
            if ((c is >= 'A' and <= 'Z') || (c is >= 'a' and <= 'z')) count++;
        return count;
    }
}
