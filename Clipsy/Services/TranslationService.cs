using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <summary>
/// Free translation via the MyMemory public endpoint. No API key needed.
/// MyMemory caps each request at 500 chars, so long text is split into
/// sentence-boundary chunks that are translated and rejoined.
/// </summary>
public static class TranslationService
{
    private const int ChunkLimit = 480; // stay safely under the 500-char cap
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<string?> TranslateAsync(string text, string from, string to)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        try
        {
            var chunks = SplitIntoChunks(text.Trim(), ChunkLimit);
            var results = new List<string>(chunks.Count);
            foreach (var chunk in chunks)
            {
                var translated = await TranslateChunkAsync(chunk, from, to);
                if (translated == null) return null;
                results.Add(translated);
            }
            return string.Join(" ", results);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Translate failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> TranslateChunkAsync(string chunk, string from, string to)
    {
        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(chunk)}&langpair={from}|{to}";
        var json = await _http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        // MyMemory returns responseStatus 429 / 206 / etc. when quota hit
        if (root.TryGetProperty("responseStatus", out var status) && status.GetInt32() != 200)
        {
            var msg = root.TryGetProperty("responseDetails", out var d) ? d.GetString() : null;
            System.Diagnostics.Debug.WriteLine($"[Clipsy] MyMemory status {status}: {msg}");
            return null;
        }
        return root.GetProperty("responseData").GetProperty("translatedText").GetString();
    }

    // Split at sentence boundaries (. ! ? newline) keeping each chunk <= limit chars.
    private static List<string> SplitIntoChunks(string text, int limit)
    {
        if (text.Length <= limit) return new List<string> { text };

        var chunks = new List<string>();
        var current = new StringBuilder();

        // Split into sentences first
        var sentences = SplitSentences(text);
        foreach (var sentence in sentences)
        {
            if (sentence.Length > limit)
            {
                // Sentence too long on its own — flush current and split by word boundary
                if (current.Length > 0) { chunks.Add(current.ToString().Trim()); current.Clear(); }
                var words = sentence.Split(' ');
                foreach (var word in words)
                {
                    if (current.Length + word.Length + 1 > limit && current.Length > 0)
                    {
                        chunks.Add(current.ToString().Trim());
                        current.Clear();
                    }
                    if (current.Length > 0) current.Append(' ');
                    current.Append(word);
                }
            }
            else if (current.Length + sentence.Length + 1 > limit)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
                current.Append(sentence);
            }
            else
            {
                if (current.Length > 0) current.Append(' ');
                current.Append(sentence);
            }
        }
        if (current.Length > 0) chunks.Add(current.ToString().Trim());
        return chunks;
    }

    private static List<string> SplitSentences(string text)
    {
        var result = new List<string>();
        var start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '.' || ch == '!' || ch == '?' || ch == '\n')
            {
                var sentence = text.Substring(start, i - start + 1).Trim();
                if (sentence.Length > 0) result.Add(sentence);
                start = i + 1;
            }
        }
        if (start < text.Length)
        {
            var tail = text.Substring(start).Trim();
            if (tail.Length > 0) result.Add(tail);
        }
        return result;
    }

    public static (string from, string to) GuessLangPair(string sample)
    {
        var ui = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        bool sampleLooksLatin = LooksLatin(sample);
        if (ui == "ru")
        {
            return sampleLooksLatin ? ("en", "ru") : ("ru", "en");
        }
        return sampleLooksLatin ? ("en", ui == "en" ? "ru" : ui) : ("auto", ui);
    }

    private static bool LooksLatin(string sample)
    {
        int latin = 0, total = 0;
        foreach (var ch in sample)
        {
            if (char.IsLetter(ch))
            {
                total++;
                if (ch <= 0x024F) latin++;
            }
        }
        return total == 0 || latin * 2 >= total;
    }
}
