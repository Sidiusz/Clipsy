using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Clipsy.Services;

public sealed record TranslationLang(string Code, string En, string Ru);

/// <summary>Translation via MyMemory (500-char) or Google (unofficial, ~4000-char);
/// long text is split on sentence boundaries and rejoined.</summary>
public static class TranslationService
{
    private const int ChunkLimitMyMemory = 480;
    private const int ChunkLimitGoogle   = 4500;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static readonly IReadOnlyList<TranslationLang> LangCatalog = new TranslationLang[]
    {
        new("en",    "English",              "Английский"),
        new("ru",    "Russian",              "Русский"),
        new("de",    "German",               "Немецкий"),
        new("fr",    "French",               "Французский"),
        new("es",    "Spanish",              "Испанский"),
        new("it",    "Italian",              "Итальянский"),
        new("pt",    "Portuguese",           "Португальский"),
        new("pl",    "Polish",               "Польский"),
        new("nl",    "Dutch",                "Нидерландский"),
        new("tr",    "Turkish",              "Турецкий"),
        new("uk",    "Ukrainian",            "Украинский"),
        new("zh-CN", "Chinese (Simplified)", "Китайский (упрощ.)"),
        new("ja",    "Japanese",             "Японский"),
        new("ko",    "Korean",               "Корейский"),
        new("ar",    "Arabic",               "Арабский"),
    };

    public static async Task<string?> TranslateAsync(string text, string from, string to,
                                                      string service = "MyMemory")
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        try
        {
            bool google = string.Equals(service, "Google", StringComparison.OrdinalIgnoreCase);
            int limit = google ? ChunkLimitGoogle : ChunkLimitMyMemory;

            // Pack as many lines as fit into each request (newlines preserved) so
            // the engine translates with full cross-line context. Translating each
            // OCR line alone produced disjointed, meaningless output.
            var chunks = PackChunks(text, limit);
            var parts = new List<string>(chunks.Count);
            foreach (var c in chunks)
            {
                if (string.IsNullOrWhiteSpace(c)) { parts.Add(c); continue; }
                var t = google
                    ? await TranslateChunkGoogleAsync(c, from, to)
                    : await TranslateChunkMyMemoryAsync(c, from, to);
                if (t == null) return null;
                parts.Add(t);
            }
            return string.Join("\n", parts);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Translate failed: {ex.Message}");
            return null;
        }
    }

    private static async Task<string?> TranslateChunkMyMemoryAsync(string chunk, string from, string to)
    {
        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(chunk)}&langpair={from}|{to}";
        var json = await _http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("responseStatus", out var status) && status.GetInt32() != 200)
        {
            var msg = root.TryGetProperty("responseDetails", out var d) ? d.GetString() : null;
            System.Diagnostics.Debug.WriteLine($"[Clipsy] MyMemory status {status}: {msg}");
            return null;
        }
        return root.GetProperty("responseData").GetProperty("translatedText").GetString();
    }

    private static async Task<string?> TranslateChunkGoogleAsync(string chunk, string from, string to)
    {
        var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={from}&tl={to}&dt=t&q={Uri.EscapeDataString(chunk)}";
        var json = await _http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
        var segs = root[0];
        var sb = new StringBuilder();
        for (int i = 0; i < segs.GetArrayLength(); i++)
        {
            var seg = segs[i];
            if (seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                sb.Append(seg[0].GetString());
        }
        return sb.ToString();
    }

    // Pack whole lines into <=limit requests, keeping newlines so the engine sees
    // context. An over-limit single line falls back to sentence chunking.
    private static List<string> PackChunks(string text, int limit)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (line.Length > limit)
            {
                if (cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); }
                result.AddRange(SplitIntoChunks(line, limit));
                continue;
            }
            int projected = cur.Length == 0 ? line.Length : cur.Length + 1 + line.Length;
            if (projected > limit && cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); }
            if (cur.Length > 0) cur.Append('\n');
            cur.Append(line);
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        return result;
    }

    // Split at sentence boundaries (. ! ? newline) keeping each chunk <= limit chars.
    private static List<string> SplitIntoChunks(string text, int limit)
    {
        if (text.Length <= limit) return new List<string> { text };

        var chunks = new List<string>();
        var current = new StringBuilder();

        var sentences = SplitSentences(text);
        foreach (var sentence in sentences)
        {
            if (sentence.Length > limit)
            {
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
