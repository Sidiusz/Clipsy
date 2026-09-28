using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;

namespace Clipsy.Services;

public sealed record OcrWord(string Text, Rect BoundsPixels);

public interface IOcrEngine
{
    /// <summary>Recognizes words in a PNG. Throws on engine failure; an empty list means no text.</summary>
    Task<IReadOnlyList<OcrWord>> RecognizeAsync(byte[] pngBytes, CancellationToken ct = default);
}

/// <summary>Raised when OCR can't run at all (no engine/language available), as opposed to finding no text.</summary>
public sealed class OcrUnavailableException(string message) : Exception(message);

public static class OcrEngineFactory
{
    public static IOcrEngine Resolve()
    {
        var configured = SettingsService.Instance.Settings.OcrEngine;
        if (string.Equals(configured, "Tesseract", StringComparison.OrdinalIgnoreCase))
        {
            if (TessdataService.InstalledSelectedCodes().Count > 0) return new TesseractOcrEngine();
            Diagnostics.Log("OCR: Tesseract selected but no language installed; using Windows OCR.");
        }
        else if (string.Equals(configured, "PPOCRv5", StringComparison.OrdinalIgnoreCase))
        {
            if (PpOcrV5Service.IsReady) return new PpOcrV5Engine();
            Diagnostics.Log("OCR: PP-OCRv5 selected but models are missing; using Windows OCR.");
        }
        return new WinRtOcrEngine();
    }
}

/// <summary>Turns positioned words into reading-order text: lines top to bottom, words in
/// script order (right-to-left for Arabic/Hebrew), no spaces between CJK characters.</summary>
public static class OcrTextLayout
{
    // Words join a line by vertical overlap, not by top edge: "в" and "PP-OCRv5" on one
    // baseline have tops a few pixels apart but overlap almost entirely.
    public static List<List<int>> GroupLines(IReadOnlyList<Rect> bounds)
    {
        var lines = new List<(List<int> Items, double Top, double Bottom)>();
        foreach (int i in Enumerable.Range(0, bounds.Count).OrderBy(i => bounds[i].Y + bounds[i].Height / 2).ThenBy(i => bounds[i].X))
        {
            var b = bounds[i];
            int best = -1;
            double bestOverlap = 0;
            for (int l = Math.Max(0, lines.Count - 3); l < lines.Count; l++)
            {
                var (_, top, bottom) = lines[l];
                double overlap = Math.Min(bottom, b.Y + b.Height) - Math.Max(top, b.Y);
                double need = 0.5 * Math.Min(b.Height, bottom - top);
                if (overlap >= need && overlap > bestOverlap) { best = l; bestOverlap = overlap; }
            }
            if (best < 0)
            {
                lines.Add((new List<int> { i }, b.Y, b.Y + b.Height));
                continue;
            }
            var line = lines[best];
            line.Items.Add(i);
            // Track the line's typical band so one tall glyph doesn't swallow the next line.
            double n = line.Items.Count;
            lines[best] = (line.Items, line.Top + (b.Y - line.Top) / n, line.Bottom + (b.Y + b.Height - line.Bottom) / n);
        }
        foreach (var line in lines) line.Items.Sort((a, c) => bounds[a].X.CompareTo(bounds[c].X));
        return lines.OrderBy(l => (l.Top + l.Bottom) / 2).Select(l => l.Items).ToList();
    }

    public static string BuildText(IReadOnlyList<OcrWord> words)
    {
        var bounds = words.Select(w => w.BoundsPixels).ToList();
        var sb = new StringBuilder();
        foreach (var line in GroupLines(bounds))
        {
            var ordered = IsRightToLeft(line.Select(i => words[i].Text)) ? Enumerable.Reverse(line).ToList() : line;
            string? prev = null;
            foreach (int i in ordered)
            {
                string text = words[i].Text;
                if (prev != null && NeedsSpace(prev, text)) sb.Append(' ');
                sb.Append(text);
                prev = text;
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static bool NeedsSpace(string left, string right)
        => left.Length == 0 || right.Length == 0 || !(IsCjk(left[^1]) && IsCjk(right[0]));

    // Chinese/Japanese are written without spaces; Korean (Hangul) uses them.
    internal static bool IsCjk(char c)
    {
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        if (c is >= '　' and <= '〿') return true;           // CJK punctuation
        if (c is >= '＀' and <= '｠') return true;           // full-width forms
        if (c is >= '぀' and <= 'ヿ') return true;           // kana
        if (c is >= '㐀' and <= '鿿') return true;           // CJK ideographs
        if (c is >= '豈' and <= '﫿') return true;           // compatibility ideographs
        return char.IsSurrogate(c) && cat == UnicodeCategory.Surrogate;
    }

    private static bool IsRightToLeft(IEnumerable<string> texts)
    {
        int rtl = 0, ltr = 0;
        foreach (var t in texts)
        foreach (var c in t)
        {
            if (c is >= '֐' and <= 'ࣿ' || c is >= 'יִ' and <= '﷿' || c is >= 'ﹰ' and <= '﻿') rtl++;
            else if (char.IsLetter(c)) ltr++;
        }
        return rtl > ltr;
    }
}
