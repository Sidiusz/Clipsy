using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Clipsy.Localization;

namespace Clipsy.Services;

/// <summary>PP-OCRv5 is the default engine: fetches its models for the user's languages in the
/// background (Windows OCR covers the gap) and offers existing Windows OCR / Tesseract users the switch once.</summary>
public static class OcrEngineMigration
{
    public const string PpEngine = "PPOCRv5";
    private const long DetectorBytes = 4_819_576;
    private static bool _downloading;

    public static void RunAtStartup()
    {
        var s = SettingsService.Instance.Settings;
        if (string.Equals(s.OcrEngine, PpEngine, StringComparison.OrdinalIgnoreCase))
        {
            if (!s.OcrEngineOfferShown)
            {
                s.OcrEngineOfferShown = true;
                SettingsService.Instance.SaveState();
            }
            if (!PpOcrV5Service.IsReady) _ = DownloadModelsAsync(DefaultModelKeys(), notify: false);
            return;
        }
        if (!s.OcrEngineOfferShown) ShowOffer();
    }

    private static void ShowOffer()
    {
        var s = SettingsService.Instance.Settings;
        bool tesseract = string.Equals(s.OcrEngine, "Tesseract", StringComparison.OrdinalIgnoreCase);
        var keys = ModelKeysForSwitch();
        int mb = (int)Math.Ceiling(DownloadBytes(keys) / 1048576.0);
        string body = string.Format(Strings.Get(tesseract ? "OcrOfferBodyTesseract" : "OcrOfferBodyWinRt"), mb);
        NotificationService.OcrEngineOffer(
            Strings.Get("OcrOfferTitle"), body,
            keepText: Strings.Get("OcrOfferKeep"), keep: Decline,
            switchText: Strings.Get("OcrOfferSwitch"), switchAction: () => _ = SwitchAsync());
    }

    private static void Decline()
    {
        SettingsService.Instance.Settings.OcrEngineOfferShown = true;
        SettingsService.Instance.SaveState();
    }

    public static async Task SwitchAsync()
    {
        var svc = SettingsService.Instance;
        var keys = ModelKeysForSwitch();
        var tessCodes = TessdataService.Catalog.Select(c => c.Code).Where(TessdataService.IsInstalled).ToList();

        svc.Settings.OcrEngineOfferShown = true;
        svc.Settings.OcrEngine = PpEngine;
        svc.Settings.TesseractLanguages = string.Empty;
        svc.Save();

        bool ok = await DownloadModelsAsync(keys, notify: true);
        // Tesseract data goes only once PP-OCRv5 works, so a failed download leaves a usable engine behind.
        if (ok)
        {
            TesseractOcrEngine.Reset(); // cached engines keep the .traineddata files open
            foreach (var code in tessCodes) TessdataService.Delete(code);
        }
    }

    private static async Task<bool> DownloadModelsAsync(IReadOnlyList<string> keys, bool notify)
    {
        if (_downloading) return false;
        _downloading = true;
        try
        {
            foreach (var key in keys.Where(k => !PpOcrV5Service.IsModelInstalled(k)))
                await PpOcrV5Service.DownloadModelAsync(key, progress: null);
            Diagnostics.Log($"OCR: PP-OCRv5 models ready ({string.Join(",", keys)})");
            if (notify) NotificationService.Prompt(Strings.Get("OcrOfferDone"));
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.Log("OCR: PP-OCRv5 model download failed; Windows OCR stays in use", ex);
            if (notify) NotificationService.Prompt(Strings.Get("OcrOfferFailed"), NotificationLevel.Error);
            return false;
        }
        finally { _downloading = false; }
    }

    private static long DownloadBytes(IEnumerable<string> keys)
    {
        long total = File.Exists(PpOcrV5Service.DetectorPath) ? 0 : DetectorBytes;
        foreach (var key in keys.Where(k => !PpOcrV5Service.IsModelInstalled(k)))
        {
            var spec = PpOcrV5Service.RecognizerModels.First(m => m.Key == key);
            if (double.TryParse(spec.ApproxSize.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var mb))
                total += (long)(mb * 1048576);
        }
        return total;
    }

    // Same languages the user had in Tesseract, plus the defaults for their UI.
    internal static IReadOnlyList<string> ModelKeysForSwitch()
    {
        var keys = new List<string>(DefaultModelKeys());
        foreach (var code in SettingsService.Instance.Settings.TesseractLanguages.Split(',',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var key = ModelKeyForTesseract(code);
            if (key != null && !keys.Contains(key)) keys.Add(key);
        }
        return keys;
    }

    internal static IReadOnlyList<string> DefaultModelKeys()
    {
        var keys = new List<string> { "en" };
        var lang = ModelKeyForLanguage(Strings.Lang) ?? ModelKeyForLanguage(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        if (lang != null && !keys.Contains(lang)) keys.Add(lang);
        return keys;
    }

    internal static string? ModelKeyForTesseract(string code) => code switch
    {
        "eng" => "en",
        "rus" or "ukr" or "bel" => "eslav",
        "bul" or "srp" or "mkd" or "kaz" or "mon" => "cyrillic",
        "chi_sim" or "chi_tra" or "jpn" => "ch",
        "kor" => "korean",
        "ara" or "fas" or "urd" => "arabic",
        "tha" => "th",
        "ell" => "el",
        "hin" or "mar" or "nep" => "devanagari",
        "tam" => "ta",
        "tel" => "te",
        "deu" or "fra" or "spa" or "ita" or "por" or "pol" or "nld" or "tur" or "ces" or "swe" or "fin"
            or "dan" or "nor" or "hun" or "ron" or "slk" or "hrv" or "slv" or "lit" or "lav" or "est"
            or "vie" or "ind" or "msa" => "latin",
        _ => null,
    };

    internal static string? ModelKeyForLanguage(string twoLetter) => twoLetter switch
    {
        "en" => "en",
        "ru" or "uk" or "be" => "eslav",
        "bg" or "sr" or "mk" or "kk" or "mn" => "cyrillic",
        "zh" or "ja" => "ch",
        "ko" => "korean",
        "ar" or "fa" or "ur" => "arabic",
        "th" => "th",
        "el" => "el",
        "hi" or "mr" or "ne" => "devanagari",
        "ta" => "ta",
        "te" => "te",
        "de" or "fr" or "es" or "it" or "pt" or "pl" or "nl" or "tr" or "cs" or "sv" or "fi" or "da"
            or "nb" or "no" or "hu" or "ro" or "sk" or "hr" or "sl" or "lt" or "lv" or "et" or "vi"
            or "id" or "ms" => "latin",
        _ => null,
    };
}
