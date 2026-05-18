using System;
using System.Collections.Generic;
using Clipsy.Services;

namespace Clipsy.Localization;

/// <summary>
/// Tiny localization helper. EN/RU only; key lookup with English
/// fallback. Bypassed if a string is missing in the chosen language.
/// </summary>
public static class Strings
{
    private static readonly Dictionary<string, Dictionary<string, string>> _all = new()
    {
        // Tray
        ["TrayTooltip"]      = new() { ["en"] = "Clipsy",                       ["ru"] = "Clipsy" },
        ["TrayCapture"]      = new() { ["en"] = "Capture Screen",               ["ru"] = "Захват экрана" },
        ["TraySettings"]     = new() { ["en"] = "Settings",                     ["ru"] = "Настройки" },
        ["TrayExit"]         = new() { ["en"] = "Exit",                         ["ru"] = "Выход" },

        // Capture overlay
        ["HintSelectArea"]   = new() { ["en"] = "Select area",                  ["ru"] = "Выделите область" },
        ["NoTextFound"]      = new() { ["en"] = "No text found",                ["ru"] = "Текст не найден" },
        ["Copied"]           = new() { ["en"] = "Copied",                       ["ru"] = "Скопировано" },
        ["TranslateUnavailable"] = new() { ["en"] = "Translation unavailable",  ["ru"] = "Перевод недоступен" },

        // Errors
        ["ErrSaveFailed"]    = new() { ["en"] = "Could not save the screenshot.", ["ru"] = "Не удалось сохранить скриншот." },
        ["ErrRecordFailed"]  = new() { ["en"] = "Recording failed to start.",   ["ru"] = "Не удалось начать запись." },
        ["ErrRecordRuntime"] = new() { ["en"] = "Recording stopped with an error.", ["ru"] = "Запись остановлена с ошибкой." },
        ["ErrOcrFailed"]     = new() { ["en"] = "OCR failed.",                  ["ru"] = "Сбой распознавания." },
        ["ErrCopyFailed"]    = new() { ["en"] = "Could not copy to clipboard.", ["ru"] = "Не удалось скопировать." },

        // Updates
        ["UpdateAvailable"]  = new() { ["en"] = "A new version of Clipsy is available.", ["ru"] = "Доступна новая версия Clipsy." },
        ["UpdateUpToDate"]   = new() { ["en"] = "You are running the latest version.", ["ru"] = "Установлена последняя версия." },
        ["UpdateCheckFailed"]= new() { ["en"] = "Update check failed.",         ["ru"] = "Проверка обновлений не удалась." },
    };

    public static string Lang { get; private set; } = "en";

    public static void Initialize()
    {
        Lang = Resolve();
    }

    private static string Resolve()
    {
        try
        {
            var setting = SettingsService.Instance.Settings.Language;
            if (setting == "ru" || setting == "en") return setting;
        }
        catch { /* settings might not be ready */ }
        var sys = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return sys == "ru" ? "ru" : "en";
    }

    public static string Get(string key)
    {
        if (!_all.TryGetValue(key, out var dict)) return key;
        if (dict.TryGetValue(Lang, out var s)) return s;
        return dict.TryGetValue("en", out var fallback) ? fallback : key;
    }
}
