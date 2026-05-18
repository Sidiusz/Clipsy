using System;
using System.Collections.Generic;
using Clipsy.Services;

namespace Clipsy.Localization;

/// <summary>
/// Tiny EN/RU localization table. Lookup by key with English fallback. The
/// dictionary is the single source of truth for any UI string that needs
/// translating; XAML elements wired to it should be tagged with x:Name and
/// have their Text/Content/ToolTip set from ApplyLocalization at runtime.
/// </summary>
public static class Strings
{
    private static readonly Dictionary<string, Dictionary<string, string>> _all = new()
    {
        // Tray
        ["TrayTooltip"]      = New("Clipsy", "Clipsy"),
        ["TrayCapture"]      = New("Capture Screen", "Захват экрана"),
        ["TraySettings"]     = New("Settings", "Настройки"),
        ["TrayExit"]         = New("Exit", "Выход"),

        // Capture overlay
        ["HintSelectArea"]      = New("Select area", "Выделите область"),
        ["NoTextFound"]         = New("No text found", "Текст не найден"),
        ["Copied"]              = New("Copied", "Скопировано"),
        ["TranslateUnavailable"]= New("Translation unavailable", "Перевод недоступен"),

        // Capture overlay - bottom toolbar tooltips
        ["TipRecord"]           = New("Record",     "Запись"),
        ["TipScreenshot"]       = New("Screenshot", "Скриншот"),
        ["TipCopy"]             = New("Copy",       "Копировать"),
        ["TipCancel"]           = New("Cancel (Esc)", "Отмена (Esc)"),

        // Capture overlay - right toolbar tooltips
        ["TipColor"]            = New("Color", "Цвет"),
        ["TipPencil"]           = New("Pencil. LMB draw, RMB erase", "Карандаш. ЛКМ рисовать, ПКМ стирать"),
        ["TipRectangle"]        = New("Rectangle", "Прямоугольник"),
        ["TipText"]             = New("Text. Click to place", "Текст. Кликните, чтобы разместить"),
        ["TipOcr"]              = New("Find text", "Найти текст"),
        ["TipBrushSize"]        = New("Brush size", "Размер кисти"),

        // OCR toolbar
        ["TipOcrSelectAll"]     = New("Select all text", "Выбрать весь текст"),
        ["TipOcrCopy"]          = New("Copy text", "Копировать текст"),
        ["TipOcrTranslate"]     = New("Translate", "Перевести"),
        ["TipOcrExit"]          = New("Exit OCR (Esc)", "Выйти из OCR (Esc)"),
        ["OcrRecognized"]       = New("Recognized text", "Распознанный текст"),
        ["TrOriginal"]          = New("Original", "Оригинал"),
        ["TrTranslation"]       = New("Translation", "Перевод"),

        // Overlay context menu
        ["MenuSelectScreen"]    = New("Select screen", "Выбрать экран"),
        ["MenuSelectAll"]       = New("Select all",    "Выбрать все"),
        ["MenuCopy"]            = New("Copy",          "Копировать"),
        ["MenuSave"]            = New("Save",          "Сохранить"),
        ["MenuSaveAs"]          = New("Save As",       "Сохранить как"),
        ["MenuClear"]           = New("Clear",         "Очистить"),
        ["MenuCancel"]          = New("Cancel",        "Отмена"),

        // Recording HUD
        ["TipPause"]            = New("Pause",                                 "Пауза"),
        ["TipStop"]             = New("Stop and save to last folder",          "Стоп и сохранить в последнюю папку"),
        ["TipSaveAs"]           = New("Stop and Save As",                      "Стоп и сохранить как"),
        ["TipDraw"]             = New("Draw",                                  "Рисовать"),
        ["TipMove"]             = New("Hold to drag region",                   "Удерживайте для переноса области"),
        ["TipLock"]             = New("Double-click to lock or unlock region", "Двойной клик для блокировки области"),

        // Settings - tabs
        ["TabGeneral"]          = New("General",  "Основные"),
        ["TabVideo"]            = New("Video",    "Видео"),
        ["TabGif"]              = New("GIF",      "GIF"),
        ["TabHotkeys"]          = New("Hotkeys",  "Горячие клавиши"),
        ["TabInfo"]             = New("Info",     "О программе"),

        // Settings - general tab labels
        ["LblLanguage"]         = New("Language",                  "Язык"),
        ["LblTheme"]            = New("Theme",                     "Тема"),
        ["LblOcrEngine"]        = New("OCR engine",                "Движок OCR"),
        ["LblScreenshotFolder"] = New("Screenshot folder",         "Папка скриншотов"),
        ["LblVideoFolder"]      = New("Video folder",              "Папка видео"),
        ["LblRememberFolder"]   = New("Remember last Save As folder", "Запоминать последнюю папку"),
        ["LblScreenshotFormat"] = New("Screenshot format",         "Формат скриншота"),
        ["LblJpgQuality"]       = New("JPEG quality",              "Качество JPEG"),
        ["LblAfterSave"]        = New("After save",                "После сохранения"),
        ["LblUpdates"]          = New("Updates",                   "Обновления"),
        ["LblCodec"]            = New("Codec",                     "Кодек"),
        ["LblResolution"]       = New("Resolution (full screen only)", "Разрешение (только полный экран)"),
        ["LblBitrate"]          = New("Bitrate (Mbps)",            "Битрейт (Мбит/с)"),
        ["LblGifColors"]        = New("Color count",               "Количество цветов"),
        ["LblGifFps"]           = New("Frame rate (fps)",          "Частота кадров"),
        ["LblGifDither"]        = New("Dithering",                 "Дизеринг"),
        ["LblRegionNote"]       = New("Region recordings use the selection size directly.",
                                      "Запись области использует размер выделения как есть."),
        ["LblHotkeyHint"]       = New("Click a binding to rebind. Esc is reserved.",
                                      "Кликните на сочетание для переназначения. Esc зарезервирован."),

        // Settings - buttons
        ["BtnBrowse"]           = New("Browse",       "Обзор"),
        ["BtnReset"]            = New("Reset",        "Сброс"),
        ["BtnClose"]            = New("Close",        "Закрыть"),
        ["BtnSave"]             = New("Save",         "Сохранить"),
        ["BtnCheckNow"]         = New("Check now",    "Проверить"),
        ["BtnCheckForUpdates"]  = New("Check for updates", "Проверить обновления"),
        ["BtnAuthor"]           = New("Author: Sidiusz", "Автор: Sidiusz"),

        // Settings - combobox items
        ["OptAuto"]             = New("Auto-detect", "Автоматически"),
        ["OptEnglish"]          = New("English",     "Английский"),
        ["OptRussian"]          = New("Russian",     "Русский"),
        ["OptDark"]             = New("Dark",        "Тёмная"),
        ["OptLight"]            = New("Light",       "Светлая"),
        ["OptTesseract"]        = New("Tesseract (default)", "Tesseract (по умолчанию)"),
        ["OptWinRtOcr"]         = New("Windows OCR", "Windows OCR"),
        ["OptPngLossless"]      = New("PNG (lossless)", "PNG (без потерь)"),
        ["OptJpgSmaller"]       = New("JPEG (smaller)", "JPEG (меньше)"),
        ["OptWebpPreview"]      = New("WebP (preview, falls back to PNG)", "WebP (предпросмотр, fallback в PNG)"),
        ["OptDoNothing"]        = New("Do nothing",  "Ничего не делать"),
        ["OptOpenFile"]         = New("Open file",   "Открыть файл"),
        ["OptOpenFolder"]       = New("Open folder", "Открыть папку"),
        ["OptHourly"]           = New("Hourly",      "Каждый час"),
        ["OptDaily"]            = New("Daily",       "Ежедневно"),
        ["OptWeekly"]           = New("Weekly",      "Еженедельно"),
        ["OptMonthly"]          = New("Monthly",     "Ежемесячно"),
        ["OptNever"]            = New("Never",       "Никогда"),

        // Settings - Info tab
        ["VersionPrefix"]       = New("Version", "Версия"),

        // Errors
        ["ErrSaveFailed"]    = New("Could not save the screenshot.", "Не удалось сохранить скриншот."),
        ["ErrRecordFailed"]  = New("Recording failed to start.",      "Не удалось начать запись."),
        ["ErrRecordRuntime"] = New("Recording stopped with an error.","Запись остановлена с ошибкой."),
        ["ErrOcrFailed"]     = New("OCR failed.",                     "Сбой распознавания."),
        ["ErrCopyFailed"]    = New("Could not copy to clipboard.",    "Не удалось скопировать."),

        // Updates
        ["UpdateAvailable"]  = New("A new version of Clipsy is available.", "Доступна новая версия Clipsy."),
        ["UpdateUpToDate"]   = New("You are running the latest version.",    "Установлена последняя версия."),
        ["UpdateCheckFailed"]= New("Update check failed.",                   "Проверка обновлений не удалась."),
    };

    private static Dictionary<string, string> New(string en, string ru)
        => new() { ["en"] = en, ["ru"] = ru };

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
