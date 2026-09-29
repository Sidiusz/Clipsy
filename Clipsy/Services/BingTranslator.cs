using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Clipsy.Services;

/// <summary>Microsoft Translator through Bing's web translator: no API key, but it needs the
/// session token embedded in the translator page, which expires after about an hour.</summary>
internal static partial class BingTranslator
{
    private const string PageUrl = "https://www.bing.com/translator";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(50);
    private static readonly SemaphoreSlim _sessionGate = new(1, 1);
    private static readonly HttpClient _http = CreateClient();
    private static Session? _session;
    private static int _requestCount;

    private sealed record Session(string Ig, string Iid, string Key, string Token, DateTime ExpiresUtc);

    private static HttpClient CreateClient()
    {
        // The page sets cookies the translate call checks.
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AutomaticDecompression = DecompressionMethods.All };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        return c;
    }

    public static async Task<TranslationResult> TranslateAsync(string text, string from, string to, CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var session = await GetSessionAsync(forceRefresh: attempt > 0, ct);
            if (session == null) return new TranslationResult(null, "TranslateUnavailable");

            var url = $"https://www.bing.com/ttranslatev3?isVertical=1&IG={session.Ig}&IID={session.Iid}.{Interlocked.Increment(ref _requestCount)}";
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["fromLang"] = from == "auto" ? "auto-detect" : ToBingCode(from),
                ["to"] = ToBingCode(to),
                ["text"] = text,
                ["token"] = session.Token,
                ["key"] = session.Key,
            });
            using var response = await _http.PostAsync(url, content, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return new TranslationResult(null, "TranslateQuota");
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                && root[0].TryGetProperty("translations", out var tr) && tr.GetArrayLength() > 0)
                return new TranslationResult(tr[0].GetProperty("text").GetString() ?? string.Empty);

            // {"statusCode":205} and friends: the session token went stale.
            int status = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("statusCode", out var sc) && sc.ValueKind == JsonValueKind.Number
                ? sc.GetInt32() : 0;
            if (status == 429) return new TranslationResult(null, "TranslateQuota");
            Diagnostics.Log($"Bing translate status {status}, attempt {attempt + 1}");
        }
        return new TranslationResult(null, "TranslateUnavailable");
    }

    private static async Task<Session?> GetSessionAsync(bool forceRefresh, CancellationToken ct)
    {
        var current = _session;
        if (!forceRefresh && current != null && current.ExpiresUtc > DateTime.UtcNow) return current;
        await _sessionGate.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _session is { } fresh && fresh.ExpiresUtc > DateTime.UtcNow) return fresh;
            var html = await _http.GetStringAsync(PageUrl, ct);
            var ig = IgRegex().Match(html);
            var iid = IidRegex().Match(html);
            var ab = AbuseRegex().Match(html);
            if (!ig.Success || !iid.Success || !ab.Success)
            {
                Diagnostics.Log("Bing translate: session parameters not found on the translator page");
                _session = null;
                return null;
            }
            _session = new Session(ig.Groups[1].Value, iid.Groups[1].Value, ab.Groups[1].Value, ab.Groups[2].Value,
                DateTime.UtcNow + TokenLifetime);
            return _session;
        }
        finally { _sessionGate.Release(); }
    }

    internal static string ToBingCode(string code) => code switch
    {
        "zh-CN" => "zh-Hans",
        "zh-TW" => "zh-Hant",
        _ => code,
    };

    [GeneratedRegex("IG:\"([^\"]+)\"")] private static partial Regex IgRegex();
    [GeneratedRegex("data-iid=\"([^\"]+)\"")] private static partial Regex IidRegex();
    [GeneratedRegex(@"params_AbusePreventionHelper\s*=\s*\[(\d+),""([^""]+)""")] private static partial Regex AbuseRegex();
}
