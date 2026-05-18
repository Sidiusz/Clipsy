using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <summary>
/// Free translation via the MyMemory public endpoint. No API key, but a
/// daily anonymous quota and short text limit (~500 chars). Good enough
/// for OCR snippets.
/// </summary>
public static class TranslationService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static async Task<string?> TranslateAsync(string text, string from, string to)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        try
        {
            var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={from}|{to}";
            var json = await _http.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var translated = doc.RootElement
                .GetProperty("responseData")
                .GetProperty("translatedText")
                .GetString();
            return translated;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Clipsy] Translate failed: {ex.Message}");
            return null;
        }
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
