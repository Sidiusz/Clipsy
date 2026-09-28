using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Clipsy.Services;

public sealed record TranslationLang(string Code, string En, string Ru);

/// <param name="ErrorKey">Localization key describing why <paramref name="Text"/> is null.</param>
public sealed record TranslationResult(string? Text, string? ErrorKey = null);

/// <summary>Translation via Google (unofficial endpoint) or MyMemory. Text leaves the machine;
/// the overlay tells the user once before the first request.</summary>
public static class TranslationService
{
    // MyMemory counts bytes (500 max); Google's form POST takes ~5000 chars.
    private const int ChunkLimitMyMemoryBytes = 480;
    private const int ChunkLimitGoogleChars = 4500;

    private static readonly HttpClient _http = CreateClient();

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

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Clipsy");
        return c;
    }

    public static async Task<TranslationResult> TranslateAsync(string text, string from, string to,
        string service = "Google", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return new TranslationResult(string.Empty);
        bool google = string.Equals(service, "Google", StringComparison.OrdinalIgnoreCase);

        // MyMemory has no working auto-detect. Text already in the target language is
        // translated the other way (Russian UI + Russian text → English).
        bool confident = true;
        string? source = from == "auto" ? DetectLanguage(text, out confident) : from;
        if (!google && from == "auto") from = source ?? "en";
        if (source != null && confident && SameLanguage(source, to)) to = SameLanguage(source, "en") ? "ru" : "en";

        try
        {
            var chunks = google
                ? PackChunks(text, s => s.Length, ChunkLimitGoogleChars)
                : PackChunks(text, s => Encoding.UTF8.GetByteCount(s), ChunkLimitMyMemoryBytes);
            var sb = new StringBuilder();
            foreach (var (chunk, separator) in chunks)
            {
                var translated = google
                    ? await TranslateChunkGoogleAsync(chunk, from, to, ct)
                    : await TranslateChunkMyMemoryAsync(chunk, from, to, ct);
                if (translated.Text == null) return translated;
                sb.Append(translated.Text).Append(separator);
            }
            return new TranslationResult(sb.ToString().TrimEnd());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new TranslationResult(null, "TranslateNetwork"); // HttpClient timeout
        }
        catch (HttpRequestException ex)
        {
            Diagnostics.Log($"Translate ({service}) failed: {ex.StatusCode} {ex.Message}");
            return new TranslationResult(null, ex.StatusCode == HttpStatusCode.TooManyRequests ? "TranslateQuota" : "TranslateNetwork");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Diagnostics.Log($"Translate ({service}) failed", ex);
            return new TranslationResult(null, "TranslateUnavailable");
        }
    }

    private static async Task<TranslationResult> TranslateChunkMyMemoryAsync(string chunk, string from, string to, CancellationToken ct)
    {
        var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(chunk)}&langpair={Uri.EscapeDataString(from)}|{Uri.EscapeDataString(to)}";
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return new TranslationResult(null, "TranslateQuota");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("quotaFinished", out var q) && q.ValueKind == JsonValueKind.True)
            return new TranslationResult(null, "TranslateQuota");
        int status = root.TryGetProperty("responseStatus", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 200;
        if (status != 200)
        {
            var details = root.TryGetProperty("responseDetails", out var d) ? d.ToString() : string.Empty;
            Diagnostics.Log($"MyMemory status {status}: {details}");
            return new TranslationResult(null, status is 403 or 429 ? "TranslateQuota" : "TranslateUnavailable");
        }
        var text = root.GetProperty("responseData").GetProperty("translatedText").GetString() ?? string.Empty;
        // Quota warnings come back as a "translation"; entities are HTML-escaped.
        if (text.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
            return new TranslationResult(null, "TranslateQuota");
        return new TranslationResult(WebUtility.HtmlDecode(text));
    }

    private static async Task<TranslationResult> TranslateChunkGoogleAsync(string chunk, string from, string to, CancellationToken ct)
    {
        // POST keeps screen text out of URLs (proxy logs) and avoids URL length limits.
        var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={Uri.EscapeDataString(from)}&tl={Uri.EscapeDataString(to)}&dt=t";
        using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("q", chunk) });
        using var response = await _http.PostAsync(url, content, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return new TranslationResult(null, "TranslateQuota");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.Array)
            return new TranslationResult(null, "TranslateUnavailable");
        var sb = new StringBuilder();
        foreach (var seg in root[0].EnumerateArray())
        {
            if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                sb.Append(seg[0].GetString());
        }
        return new TranslationResult(sb.ToString());
    }

    private static bool SameLanguage(string a, string b)
        => string.Equals(a.Split('-')[0], b.Split('-')[0], StringComparison.OrdinalIgnoreCase);

    /// <summary>Packs whole lines into requests (context helps), remembering the separator to restore
    /// after each chunk: a newline between lines, a space inside a line split at sentence ends.</summary>
    internal static List<(string Chunk, string Separator)> PackChunks(string text, Func<string, int> measure, int limit)
    {
        var result = new List<(string, string)>();
        var cur = new StringBuilder();
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine;
            if (measure(line) > limit)
            {
                if (cur.Length > 0) { result.Add((cur.ToString(), "\n")); cur.Clear(); }
                var parts = SplitLongLine(line, measure, limit);
                for (int i = 0; i < parts.Count; i++)
                    result.Add((parts[i], i == parts.Count - 1 ? "\n" : " "));
                continue;
            }
            string candidate = cur.Length == 0 ? line : cur + "\n" + line;
            if (cur.Length > 0 && measure(candidate) > limit)
            {
                result.Add((cur.ToString(), "\n"));
                cur.Clear();
                candidate = line;
            }
            cur.Clear().Append(candidate);
        }
        if (cur.Length > 0) result.Add((cur.ToString(), "\n"));
        return result;
    }

    // Sentence boundaries first, then words, then hard cuts for unbroken text (CJK).
    private static List<string> SplitLongLine(string line, Func<string, int> measure, int limit)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length > 0) pieces.Add(current.ToString().Trim());
            current.Clear();
        }
        foreach (var token in Tokenize(line))
        {
            if (measure(token) > limit)
            {
                Flush();
                var hard = new StringBuilder();
                foreach (var ch in token)
                {
                    if (hard.Length > 0 && measure(hard.ToString() + ch) > limit) { pieces.Add(hard.ToString()); hard.Clear(); }
                    hard.Append(ch);
                }
                if (hard.Length > 0) current.Append(hard);
                continue;
            }
            if (current.Length > 0 && measure(current.ToString() + token) > limit) Flush();
            current.Append(token);
        }
        Flush();
        pieces.RemoveAll(p => p.Length == 0);
        return pieces;
    }

    // Sentences (keeping their trailing space) or, inside an over-long sentence, words.
    private static IEnumerable<string> Tokenize(string line)
    {
        int start = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] is '.' or '!' or '?' or '。' or '！' or '？' or ' ')
            {
                yield return line.Substring(start, i - start + 1);
                start = i + 1;
            }
        }
        if (start < line.Length) yield return line[start..];
    }

    /// <summary>Script-based guess; Latin languages are told apart by their distinctive letters.
    /// Plain Latin without such letters is reported as English with low confidence.</summary>
    internal static string? DetectLanguage(string text, out bool confident)
    {
        confident = true;
        int latin = 0, cyr = 0, han = 0, kana = 0, hangul = 0, arabic = 0;
        int uk = 0, de = 0, fr = 0, es = 0, pt = 0, pl = 0, tr = 0;
        foreach (var c in text)
        {
            if (c is >= '぀' and <= 'ヿ') kana++;
            else if (c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿') han++;
            else if (c is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ') hangul++;
            else if (c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ') arabic++;
            else if (c is >= 'Ѐ' and <= 'ӿ')
            {
                cyr++;
                if ("іїєґІЇЄҐ".IndexOf(c) >= 0) uk++;
            }
            else if (char.IsLetter(c) && c <= 'ɏ')
            {
                latin++;
                if ("äöüßÄÖÜ".IndexOf(c) >= 0) de++;
                if ("éèêëàâçœîïôûùÉÈÊÀÇ".IndexOf(c) >= 0) fr++;
                if ("ñ¿¡áíóúÑÁÍÓÚ".IndexOf(c) >= 0) es++;
                if ("ãõÃÕ".IndexOf(c) >= 0) pt++;
                if ("ąęłśźżńćĄĘŁŚŹŻŃĆ".IndexOf(c) >= 0) pl++;
                if ("ışğİŞĞ".IndexOf(c) >= 0) tr++;
            }
        }
        if (kana > 0) return "ja";
        int max = Math.Max(Math.Max(latin, cyr), Math.Max(Math.Max(han, hangul), arabic));
        if (max == 0) return null;
        if (max == han) return "zh-CN";
        if (max == hangul) return "ko";
        if (max == arabic) return "ar";
        if (max == cyr) return uk > 0 ? "uk" : "ru";
        var marks = new (string Code, int Count)[] { ("de", de), ("fr", fr), ("es", es), ("pt", pt), ("pl", pl), ("tr", tr) };
        var best = ("en", 0);
        foreach (var m in marks)
            if (m.Count > best.Item2) best = m;
        confident = best.Item2 > 0 || latin >= 20;
        return best.Item1;
    }
}
