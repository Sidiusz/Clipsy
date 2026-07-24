using System;
using System.Collections.Generic;
using Clipsy.Services;

namespace Clipsy.Localization;

/// <summary>Tiny EN/RU localization table; lookup by key with English fallback.
/// Single source of truth for translatable UI strings (set via ApplyLocalization).</summary>
public static class Strings
{
    private static readonly Dictionary<string, Dictionary<string, string>> _all = new()
    {
        // Tray
        ["TrayTooltip"]      = New("Clipsy", "Clipsy"),
        ["TrayCapture"]          = New("Capture screen",              "Захват экрана"),
        ["TrayRecord"]           = New("Record region",               "Запись области"),
        ["TrayOpenFolder"]       = New("Open captures folder",        "Открыть папку"),
        ["TrayOpenScreenshots"]  = New("Open screenshots folder",     "Открыть папку скриншотов"),
        ["TrayOpenVideos"]       = New("Open videos folder",          "Открыть папку с видео"),
        ["TraySettings"]         = New("Settings",                    "Настройки"),
        ["TrayAbout"]            = New("About",                       "О программе"),
        ["TrayExit"]             = New("Exit",                        "Выход"),
        ["TrayReady"]            = New("ready",                       "готов"),
        ["TrayUpdateChecking"]   = New("Checking for updates…",       "Проверка обновлений…"),
        ["TrayUpdateAvailable"]  = New("Update available — click to download", "Доступно обновление — нажмите, чтобы скачать"),
        ["TrayUpdateDownloading"]= New("Downloading update…",         "Скачивание обновления…"),
        ["TrayUpdateInstall"]    = New("Update ready — click to install", "Обновление готово — нажмите, чтобы установить"),
        ["TrayUpdateFailed"]     = New("Update check failed — click to retry", "Ошибка обновления — нажмите для повтора"),

        // Capture overlay
        ["HintSelectArea"]      = New("Select area", "Выделите область"),
        ["HintFullScreen"]      = New("full screen", "весь экран"),
        ["HintCancel"]          = New("cancel", "отмена"),
        ["FilterPlaceholder"]   = New("Filter…", "Фильтр…"),
        ["NoTextFound"]         = New("No text found", "Текст не найден"),
        ["OcrLangHint"]         = New("Looks like {0} — language pack not installed. Add it in Settings.", "Похоже на {0} — языковой пакет не установлен. Добавьте его в настройках."),
        ["Copied"]              = New("Copied", "Скопировано"),
        ["TranslateUnavailable"]= New("Translation unavailable", "Перевод недоступен"),

        // Capture overlay - bottom toolbar tooltips
        ["TipRecord"]           = New("Record",     "Запись"),
        ["TipScreenshot"]       = New("Screenshot", "Скриншот"),
        ["TipCopy"]             = New("Copy",       "Копировать"),
        ["TipCancel"]           = New("Cancel (Esc)", "Отмена (Esc)"),

        // Capture overlay - right toolbar tooltips
        ["TipColor"]            = New("Color", "Цвет"),
        ["TipColorApply"]       = New("Apply", "Применить"),
        ["TipColorCancel"]      = New("Cancel", "Отмена"),
        ["TipEyedropper"]       = New("Pick color from screen", "Взять цвет с экрана"),
        ["TipPencil"]           = New("Pencil. LMB draw, RMB erase", "Карандаш. ЛКМ рисовать, ПКМ стирать"),
        ["TipRectangle"]        = New("Rectangle", "Прямоугольник"),
        ["TipEllipse"]          = New("Ellipse", "Эллипс"),
        ["TipLine"]             = New("Line", "Линия"),
        ["TipArrow"]            = New("Arrow", "Стрелка"),
        ["TipText"]             = New("Text. Click to place", "Текст. Кликните, чтобы разместить"),
        ["TipShapes"]           = New("Shapes", "Фигуры"),
        ["TipOcr"]              = New("Find text", "Найти текст"),
        ["TipMove"]             = New("Move objects. Click to select, click again to cycle overlaps, drag to move.", "Перемещение объектов. Клик — выбрать, повторный клик — следующий под курсором, тянуть — двигать."),
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
        ["MenuSaveAs"]          = New("Save As…",      "Сохранить как…"),
        ["MenuClear"]           = New("Clear drawings","Очистить рисунки"),
        ["MenuCancel"]          = New("Cancel",        "Отмена"),

        // Recording HUD
        ["TipMicActive"]        = New("Microphone on — click to mute",         "Микрофон включён — кликните для отключения"),
        ["TipMicMuted"]         = New("Microphone muted — click to unmute",    "Микрофон выключен — кликните для включения"),
        ["TipPause"]            = New("Pause",                                 "Пауза"),
        ["TipStop"]             = New("Stop and save to last folder",          "Стоп и сохранить в последнюю папку"),
        ["TipSaveAs"]           = New("Stop and Save As",                      "Стоп и сохранить как"),
        ["TipDraw"]             = New("Draw. RMB: erase. Shift+RMB: erase entire stroke.",
                                       "Рисовать. ПКМ - стирать, Shift+ПКМ - стереть всю фигуру."),
        ["TipMove"]             = New("Hold to drag region",                   "Удерживайте для переноса области"),
        ["TipLock"]             = New("Double-click to lock or unlock region", "Двойной клик для блокировки области"),
        ["TipCancelRec"]        = New("Cancel recording (discard)",            "Отменить запись (удалить)"),
        ["WarnHotkeyConflict"]  = New("Capture hotkey is in use by another app. Open Settings → Hotkeys to rebind, or disable the Win11 \"Use PrintScreen to open Snipping\" option.",
                                       "Горячая клавиша занята другим приложением. Откройте Настройки → Горячие клавиши или отключите в Win11 «Использовать PrintScreen для запуска Snipping»."),
        ["AlreadyRunningTitle"] = New("Clipsy is already running",             "Clipsy уже запущен"),
        ["AlreadyRunningBody"]  = New("Another instance is active. Look for Clipsy in the system tray.",
                                       "Другая копия уже работает. Найдите Clipsy в системном трее."),

        // Settings - tabs
        ["TabGeneral"]          = New("General",  "Основные"),
        ["TabOcr"]              = New("Recognition and Translation", "Распознавание и перевод"),
        ["TabVideo"]            = New("Video",    "Видео"),
        ["TabGif"]              = New("GIF",      "GIF"),
        ["TabHotkeys"]          = New("Hotkeys",  "Горячие клавиши"),
        ["TabNotifications"]    = New("Notifications", "Уведомления"),
        ["TabInfo"]             = New("Info",     "О программе"),
        ["SubNotifications"]    = New("Pop-ups appear bottom-right and auto-dismiss in 4 s. Choose which events show one.", "Всплывашки появляются снизу справа и закрываются через 4 с. Выберите, какие события их показывают."),
        ["LblNotifyVideo"]      = New("Recording saved",  "Запись сохранена"),
        ["LblNotifyClipboard"]  = New("Copied to clipboard", "Скопировано в буфер обмена"),

        // Settings - general tab labels
        ["LblLanguage"]         = New("Language",                  "Язык"),
        ["LblTheme"]            = New("Theme",                     "Тема"),
        ["LblOcrEngine"]        = New("OCR Text Recognition",                "OCR Распознавание текста"),
        ["LblScreenshotFolder"] = New("Screenshot folder",         "Папка скриншотов"),
        ["LblVideoFolder"]      = New("Video folder",              "Папка видео"),
        ["LblRememberFolder"]   = New("Remember last Save As folder", "Запоминать последнюю папку"),
        ["LblAutostart"]        = New("Start with Windows", "Запускать с Windows"),
        ["HelperAutostart"]     = New("Launch Clipsy when you sign in.", "Запускать Clipsy при входе в систему."),
        ["LblScreenshotFormat"] = New("Screenshot format",         "Формат скриншота"),
        ["LblVideoFormat"]      = New("Video format",             "Формат видео"),
        ["HelperVideoFormat"]   = New("Format used for automatic saves. Save As lets you pick a different one.", "Формат при автоматическом сохранении. В «Сохранить как» можно выбрать другой."),
        ["LblJpgQuality"]       = New("JPEG quality",              "Качество JPEG"),
        ["LblScreenshotCursor"] = New("Include cursor",            "Включить курсор"),
        ["HelperScreenshotCursor"] = New("Show the mouse pointer in screenshots. Default: off.", "Показывать указатель мыши на скриншотах. По умолчанию: выкл."),
        ["LblDynamicIslands"]   = New("Dynamic tool islands",      "Динамичные острова инструментов"),
        ["HelperDynamicIslands"] = New("Toolbars dock to the corner where the selection drag ends.", "Панели прикрепляются к углу, в котором завершено выделение."),
        ["LblVideoCursor"]      = New("Include cursor",            "Включить курсор"),
        ["HelperVideoCursor"]   = New("Show the mouse pointer in recordings. Default: on.", "Показывать указатель мыши в записях. По умолчанию: вкл."),
        ["LblAfterSave"]        = New("After-save behavior",       "Поведение после сохранения"),
        ["HelperAfterSave"]     = New("What Clipsy does right after a screenshot or recording is saved.", "Что Clipsy делает сразу после сохранения скриншота или записи."),
        ["LblUpdates"]          = New("Updates",                   "Обновления"),
        ["HelperUpdates"]       = New("How often Clipsy checks for a new version.", "Как часто Clipsy проверяет наличие новой версии."),
        ["LblAppManagement"]    = New("App management",            "Управление приложением"),
        ["LblCodec"]            = New("Codec",                     "Кодек"),
        ["CodecH264Desc"]       = New("Wide compatibility, hardware-accelerated", "Широкая совместимость, аппаратное ускорение"),
        ["CodecH265Desc"]       = New("Smaller files, slower encoding", "Файлы меньше, кодирование медленнее"),
        ["CodecVp9Desc"]        = New("Open codec, common on the web",  "Открытый кодек, широко используется в вебе"),
        ["CodecAv1Desc"]        = New("Smallest files, very slow encoding", "Самые маленькие файлы, очень медленное кодирование"),
        ["SettingsHeader"]      = New("SETTINGS",                  "НАСТРОЙКИ"),
        ["LblResolution"]       = New("Resolution",                "Разрешение"),
        ["LblVideoFps"]         = New("Frame rate",                "Частота кадров"),
        ["HelperVideoFps"]      = New("How many frames per second are recorded. Native follows the display refresh rate. Default: 60",
                                      "Сколько кадров в секунду пишется в видео. «Родное» — частота обновления монитора. По умолчанию: 60"),
        ["LblBitrate"]          = New("Bitrate",                   "Битрейт"),
        ["LblGifColors"]        = New("Color count",               "Количество цветов"),
        ["LblGifFps"]           = New("Frame rate (fps)",          "Частота кадров"),
        ["LblGifDither"]        = New("Dithering",                 "Дизеринг"),
        ["LblRegionNote"]       = New("Maximum video height: larger recordings are scaled down to it, smaller ones keep their size. Default: 1080p",
                                      "Максимальная высота видео: записи крупнее уменьшаются до неё, меньшие остаются как есть. По умолчанию: 1080p"),
        ["LblHotkeyHint"]       = New("Click a binding to rebind. Esc is reserved.",
                                      "Кликните на сочетание для переназначения. Esc зарезервирован."),

        // Settings - buttons
        ["BtnBrowse"]           = New("Browse",       "Обзор"),
        ["BtnReset"]            = New("Reset",        "Сброс"),
        ["BtnConfirm"]          = New("Confirm",      "Подтвердить"),
        ["BtnCancel"]           = New("Cancel",       "Отмена"),
        ["ConfirmResetTitle"]   = New("Reset all settings?", "Сбросить все настройки?"),
        ["ConfirmResetBody"]    = New("Every option goes back to its default value. This cannot be undone.",
                                      "Все параметры вернутся к значениям по умолчанию. Это действие необратимо."),
        ["ConfirmDiscardTitle"] = New("Discard unsaved changes?", "Сбросить несохранённые изменения?"),
        ["ConfirmDiscardBody"]  = New("You have unsaved changes. Closing now will lose them.",
                                      "У вас есть несохранённые изменения. При закрытии они будут потеряны."),
        ["BtnClose"]            = New("Close",        "Закрыть"),
        ["BtnSave"]             = New("Save changes", "Сохранить"),
        ["SettingsSaved"]       = New("Settings saved", "Настройки сохранены"),
        ["BtnCheckNow"]         = New("Check now",    "Проверить"),
        ["BtnCheckForUpdates"]  = New("Check for updates", "Проверить обновления"),
        ["BtnAuthor"]           = New("Author: Sidiusz", "Автор: Sidiusz"),

        // Settings - combobox items
        ["OptAuto"]             = New("Auto-detect", "Автоматически"),
        ["OptEnglish"]          = New("English",     "Английский"),
        ["OptRussian"]          = New("Russian",     "Русский"),
        ["OptDark"]             = New("Dark",        "Тёмная"),
        ["OptLight"]            = New("Light",       "Светлая"),
        ["OptTesseract"]        = New("Tesseract", "Tesseract"),
        ["OptWinRtOcr"]         = New("Windows OCR", "Windows OCR"),
        ["SuffixDefault"]       = New("(default)", "(по умолчанию)"),
        ["OptPngLossless"]      = New("PNG (higher quality)", "PNG (качественнее)"),
        ["OptJpgSmaller"]       = New("JPEG (smaller size)", "JPEG (меньше размер)"),
        ["OptWebpPreview"]      = New("WebP (web-friendly)", "WebP (для веба)"),
        ["OptVidMp4"]           = New("MP4 (recommended)", "MP4 (рекомендуется)"),
        ["OptVidAvi"]           = New("AVI", "AVI"),
        ["OptVidMkv"]           = New("MKV", "MKV"),
        ["OptVidGif"]           = New("GIF (animated)", "GIF (анимация)"),
        ["OptDoNothing"]        = New("Do nothing",  "Ничего не делать"),
        ["OptOpenFile"]         = New("Open file",   "Открыть файл"),
        ["OptOpenFolder"]       = New("Open folder", "Открыть папку"),
        ["OptHourly"]           = New("Hourly",      "Каждый час"),
        ["OptDaily"]            = New("Daily",       "Ежедневно"),
        ["OptWeekly"]           = New("Weekly",      "Еженедельно"),
        ["OptMonthly"]          = New("Monthly",     "Ежемесячно"),
        ["OptNever"]            = New("Never",       "Никогда"),
        ["OptResNative"]        = New("Native",      "Родное"),

        // Settings - Info tab
        ["VersionPrefix"]       = New("v", "v"),

        // Errors
        ["ErrSaveFailed"]    = New("Could not save the screenshot.", "Не удалось сохранить скриншот."),
        ["ErrRecordFailed"]  = New("Recording failed to start.",      "Не удалось начать запись."),
        ["ErrRecordRuntime"] = New("Recording stopped with an error.","Запись остановлена с ошибкой."),
        ["ErrOcrFailed"]     = New("OCR failed.",                     "Сбой распознавания."),
        ["ErrCopyFailed"]    = New("Could not copy to clipboard.",    "Не удалось скопировать."),
        ["ErrGifConversionFailed"] = New("GIF conversion failed.",    "Не удалось конвертировать в GIF."),
        ["ErrFFmpegDownloadFailed"] = New("FFmpeg download failed.",  "Не удалось скачать FFmpeg."),

        // Updates
        ["UpdateAvailable"]  = New("A new version of Clipsy is available.", "Доступна новая версия Clipsy."),
        ["UpdateUpToDate"]   = New("You are running the latest version.",    "Установлена последняя версия."),
        ["UpdateCheckFailed"]= New("Update check failed.",                   "Проверка обновлений не удалась."),
        ["WarnCodecFallback"] = New("H.265 is not supported by your hardware. Recording in H.264.",
                                     "H.265 не поддерживается оборудованием. Запись в H.264."),
        ["HelperCodecVp9Av1"] = New("With FFmpeg installed you get VP9, AV1 and noticeably better GIF quality.",
                                     "С установленным FFmpeg доступны VP9, AV1 и заметно лучшее качество GIF."),
        // Microphone settings
        ["LblMicrophone"]       = New("Microphone",                            "Микрофон"),
        ["LblMicEnabled"]       = New("Record microphone",                     "Записывать микрофон"),
        ["HelperMic"]           = New("Record the microphone along with the screen. Default: off.", "Записывать микрофон вместе с экраном. По умолчанию: выкл."),
        ["LblMicDevice"]        = New("Input device",                          "Устройство ввода"),
        ["HelperMicDevice"]     = New("Leave on default to use the system microphone.", "Оставьте «по умолчанию», чтобы использовать системный микрофон."),
        ["OptMicDefault"]       = New("Default system microphone",             "Системный микрофон по умолчанию"),

        ["LblFfmpegSection"]  = New("FFmpeg",                    "FFmpeg"),
        ["FfmpegInstalled"]   = New("Installed",                 "Установлен"),
        ["FfmpegNotInstalled"]= New("Not installed",             "Не установлен"),
        ["BtnInstallFfmpeg"]  = New("Install (~150 MB)",         "Установить (~150 МБ)"),
        ["BtnDeleteFfmpeg"]   = New("Remove",                    "Удалить"),
        ["BtnCancelFfmpeg"]   = New("Cancel",                    "Отмена"),
        ["FfmpegDownloading"] = New("Downloading FFmpeg...",     "Загрузка FFmpeg..."),
        ["FfmpegExtracting"]  = New("Extracting...",             "Распаковка..."),
        ["FfmpegDone"]        = New("FFmpeg installed.",         "FFmpeg установлен."),
        ["ErrFfmpegFailed"]   = New("FFmpeg installation failed.","Не удалось установить FFmpeg."),
        ["WarnNoFfmpeg"]      = New("VP9 / AV1 requires FFmpeg. Install it in Video settings.",
                                     "VP9 / AV1 требует FFmpeg. Установите его в настройках видео."),
        ["NoteAv1Slow"]       = New("AV1 is CPU-intensive; real-time capture may drop frames on slower hardware.",
                                     "AV1 нагружает CPU; на медленном железе возможны пропуски кадров."),

        // Settings - subtitles, helpers, info, author
        ["TitleBarSubtitle"]      = New("Settings",                "Настройки"),
        ["SubGeneral"]            = New("Language, theme, and where files end up.",       "Язык, тема и место сохранения файлов."),
        ["SubOcr"]                = New("Text recognition engine, language files, and translation service.",
                                        "Движок распознавания текста, языковые файлы и сервис перевода."),
        ["SubVideo"]              = New("Codec, resolution, and bitrate for screen recordings.", "Кодек, разрешение и битрейт для записи."),
        ["SubGif"]                = New("Output settings when exporting recordings as animated GIF.", "Параметры вывода для экспорта в анимированный GIF."),
        ["HelperLanguage"]        = New("Auto-detect follows the system language.",       "Автоопределение использует язык системы."),
        ["HelperTheme"]           = New("Applies instantly, no restart needed.",          "Применяется сразу, без перезапуска."),
        ["HelperOcr"]             = New("Engine that recognizes text in captures.", "Движок, который распознаёт текст на снимках."),
        ["LblTranslation"]        = New("Translation",          "Перевод"),
        ["HelperTranslation"]     = New("Service and language pair used when translating recognized text.", "Сервис и языковая пара для перевода распознанного текста."),
        ["LblTranslateService"]   = New("Service",             "Сервис"),
        ["LblTranslateFrom"]      = New("Translate from",                "Перевод с"),
        ["HelperTranslateFrom"]   = New("Source language of the recognized text.", "Язык-источник распознанного текста."),
        ["LblTranslateTo"]        = New("Translate to",                  "Перевод на"),
        ["HelperTranslateTo"]     = New("Language the text is translated into.", "Язык, на который переводится текст."),
        ["OptMyMemory"]           = New("MyMemory (free)",     "MyMemory (бесплатно)"),
        ["OptGoogle"]             = New("Google Translate",    "Google Переводчик"),
        ["LangUiDefault"]         = New("Interface language",  "Язык интерфейса"),
        ["LangAutoDetect"]        = New("Auto-detect",         "Автоопределение"),

        ["LblTessLang"]           = New("Tesseract language packs", "Языковые пакеты Tesseract"),
        ["HelperTessLang"]        = New("Languages Tesseract can recognize offline (~{0}-{1} {2} each).", "Языки, которые Tesseract распознаёт офлайн (~{0}-{1} {2} каждый)."),
        ["UnitMB"]                = New("MB", "МБ"),
        ["BtnInstall"]            = New("Install", "Установить"),
        ["BtnDelete"]             = New("Delete",  "Удалить"),
        ["TessInstalling"]        = New("Downloading...", "Загрузка..."),
        ["TessInstalled"]         = New("Installed", "Установлено"),
        ["TessNoLangs"]           = New("No languages installed - falling back to Windows OCR.", "Языки не установлены - используется Windows OCR."),
        ["TessNotInstalledHint"]  = New("Install this language before you can select it.", "Установите язык, чтобы выбрать его."),
        ["ErrTessDownload"]       = New("Download failed.", "Ошибка загрузки."),
        ["LblRememberFolder"]     = New("Remember last Save As folder",                   "Запоминать последнюю папку «Сохранить как»"),
        ["HelperRemember"]        = New("New captures go to the folder you saved in last. Default: on.", "Новые захваты сохраняются в последнюю использованную папку. По умолчанию: вкл."),
        ["HelperCodec"]           = New("How the video is compressed. Affects file size, encoding speed and compatibility.",       "Способ сжатия видео. Влияет на размер файла, скорость кодирования и совместимость."),
        ["HelperBitrate"]         = New("Amount of data per second of video: more means sharper picture and bigger file. The cap depends on resolution. Default: 8 Mbps", "Объём данных на секунду видео: больше — чётче картинка и больше файл. Потолок зависит от разрешения. По умолчанию: 8 Мбит/с"),
        ["HelperGifColors"]       = New("Palette size of the GIF: fewer colors mean a smaller file. Default: 256",  "Размер палитры GIF: меньше цветов — меньше файл. По умолчанию: 256"),
        ["HelperGifFps"]          = New("How many frames per second the GIF contains. Default: 12",   "Сколько кадров в секунду содержит GIF. По умолчанию: 12"),
        ["HelperGifDither"]       = New("Blends palette colors to hide banding; the file gets a bit larger. Default: on.", "Смешивает цвета палитры, скрывая полосы на градиентах; файл немного больше. По умолчанию: вкл."),
        ["LblBuilt"]              = New("Built {0}",                                       "Сборка от {0}"),
        ["TipSelectScreenDouble"] = New("To grab a whole screen, double-click it - or right-click → Select screen.", "Чтобы захватить весь экран, дважды кликните по нему - или ПКМ → «Выбрать экран»."),
        ["TipSelectAll"]          = New("Ctrl+A or right-click → Select all grabs every monitor at once.", "Ctrl+A или ПКМ → «Выделить всё» захватывает все мониторы сразу."),
        ["TipOcrEngine"]          = New("For more accurate text recognition, switch the OCR engine to Tesseract in OCR settings.", "Для более точного распознавания текста смените движок OCR на Tesseract в настройках OCR."),
        ["TipOcrLang"]            = New("Tesseract only reads the languages you download - add them under OCR.", "Tesseract распознаёт только скачанные языки - добавьте их в разделе OCR."),
        ["TipTranslate"]          = New("After scanning text you can translate it right in the capture window.", "После распознавания текст можно сразу перевести прямо в окне захвата."),
        ["TipEyedropper"]         = New("Use the eyedropper to pick any color from the screen while drawing.", "Пипеткой можно взять любой цвет с экрана прямо во время рисования."),
        ["TipResizeHandles"]      = New("Drag the handles on the selection edges to fine-tune the captured area.", "Тяните маркеры по краям выделения, чтобы точно подогнать область захвата."),
        ["TipCopySaveKeys"]       = New("With a selection: Ctrl+C copies, Ctrl+S saves, Ctrl+Shift+S saves as.", "При выделении: Ctrl+C - копировать, Ctrl+S - сохранить, Ctrl+Shift+S - сохранить как."),
        ["TipClearDrawings"]      = New("Right-click → Clear drawings removes annotations without losing the selection.", "ПКМ → «Очистить рисунки» убирает пометки, не сбрасывая выделение."),
        ["TipEscCancel"]          = New("Press Esc to cancel a capture at any time.", "Нажмите Esc, чтобы отменить захват в любой момент."),
        ["TipHotkeyRebind"]       = New("Don't like PrtSc? Rebind every shortcut under Hotkeys.", "Не нравится PrtSc? Переназначьте любые клавиши в разделе «Горячие клавиши»."),
        ["TipRecordRegion"]       = New("Pick a region, then hit record on the toolbar to capture video.", "Выделите область и нажмите запись на панели, чтобы снять видео."),
        ["TipMicToggle"]          = New("Assign a hotkey to mute or unmute the microphone mid-recording.", "Назначьте клавишу, чтобы выключать или включать микрофон прямо во время записи."),
        ["TipSilentSave"]         = New("The silent-save hotkey stops a recording and saves it without a dialog.", "Хоткей «без диалога» останавливает запись и сохраняет её без окна."),
        ["TipGifExport"]          = New("Choose GIF as the video format to export a short animation.", "Выберите формат GIF, чтобы экспортировать короткую анимацию."),
        ["TipGifSize"]            = New("Fewer GIF colors and a lower FPS make a much smaller file.", "Меньше цветов и FPS у GIF - заметно меньше файл."),
        ["TipFfmpeg"]             = New("AVI and MKV need FFmpeg; without it recordings fall back to MP4.", "Для AVI и MKV нужен FFmpeg; без него запись сохранится как MP4."),
        ["TipJpgQuality"]         = New("Save screenshots as JPG to shrink files - tune the quality slider.", "Сохраняйте скриншоты в JPG для меньшего размера - регулируйте ползунок качества."),
        ["TipAutostart"]          = New("Turn on autostart so Clipsy is ready in the tray after every reboot.", "Включите автозапуск, чтобы Clipsy был в трее после каждой перезагрузки."),
        ["TipAfterSave"]          = New("Set an after-save action to auto-open the file or its folder.", "Задайте действие после сохранения, чтобы автоматически открывать файл или его папку."),
        ["TipLabel"]              = New("TIP",                                             "СОВЕТ"),
        ["SectionLabelAction"]    = New("ACTION",                                          "ДЕЙСТВИЕ"),
        ["SectionLabelBinding"]   = New("BINDING",                                         "КОМБИНАЦИЯ"),
        ["LblLikeClipsy"]         = New("Like Clipsy?",                                    "Нравится Clipsy?"),
        ["LblLikeClipsyHint"]     = New("Star it on GitHub - that's enough.",              "Поставьте звезду на GitHub - этого достаточно."),
        ["LblGithubLine"]         = New("Source, issues, releases",                        "Исходники, баги, релизы."),
        ["LblUpdateStatus"]       = New("You are on the latest version.",                  "Установлена последняя версия."),
        ["LblAuthorHeader"]       = New("Author",                                          "Автор"),
        ["LblMit"]                = New("CPIUL-1.0 license",                               "Лицензия CPIUL-1.0"),
        ["BtnStar"]               = New("Star",                                            "Star"),
        ["BtnOpen"]               = New("Open",                                            "Открыть"),
        ["BtnSaveChanges"]        = New("Save changes",                                    "Сохранить"),

        // Hotkey row labels
        ["HkOpenCapture"]         = New("Open capture overlay",                            "Открыть выделение"),
        ["HkSaveSilent"]          = New("Save screenshot (silent)",                        "Сохранить скриншот (без диалога)"),
        ["HkCopy"]                = New("Copy to clipboard",                               "Скопировать в буфер"),
        ["HkUndo"]                = New("Undo",                                            "Отменить"),
        ["HkRedo"]                = New("Redo",                                            "Повторить"),
        ["HkSelectAll"]           = New("Select all",                                      "Выделить всё"),
        ["HkRecordSave"]          = New("Save recording (silent)",                         "Сохранить запись (без диалога)"),
        ["HkMicToggle"]           = New("Mute / unmute microphone",                        "Выкл/вкл микрофон"),
        ["HkPressKeys"]           = New("Press keys...",                                   "Нажмите клавиши..."),

        // Notifications shown in Settings
        ["NotifySaved"]           = New("Settings saved.",                                 "Настройки сохранены."),
        ["NotifyReset"]           = New("Settings reset to defaults.",                     "Настройки сброшены по умолчанию."),
        ["NotifyUnsaved"]         = New("You have unsaved changes.",                       "Есть несохранённые изменения."),
        ["NotifyUpdateChecking"]  = New("Checking for updates...",                         "Проверка обновлений..."),
        ["NotifyUpdateUpToDate"]  = New("You are on the latest version.",                  "Установлена последняя версия."),
        ["NotifyUpdateAvailable"] = New("Update available: {0}",                           "Доступно обновление: {0}"),
        ["NotifyUpdateFailed"]    = New("Update check failed.",                            "Проверка обновлений не удалась."),
        ["NotifySaveFailed"]      = New("Could not save settings.",                        "Не удалось сохранить настройки."),

        // Notification settings labels
        ["LblNotifyMaster"]         = New("Show notifications",                            "Показывать уведомления"),
        ["LblNotifyScreenshot"]     = New("Screenshot saved",                              "Скриншот сохранён"),
        ["LblNotifyErrors"]         = New("Errors",                                        "Ошибки"),
        ["LblNotifyUpdate"]         = New("Update available",                              "Доступно обновление"),
        ["LblNotifyHints"]          = New("Hints and info",                                "Подсказки и информация"),

        // Toast content
        ["ToastScreenshotSaved"]    = New("Screenshot saved",                              "Скриншот сохранён"),
        ["ToastVideoSaved"]         = New("Recording saved",                               "Запись сохранена"),
        ["ToastCopied"]             = New("Copied to clipboard",                           "Скопировано в буфер обмена"),
        ["ToastOpenFile"]           = New("Open file",                                     "Открыть файл"),
        ["ToastOpenFolder"]         = New("Open folder",                                   "Открыть папку"),
        ["ToastDownload"]           = New("Download update",                               "Скачать обновление"),
        ["ToastSkipVersion"]        = New("Skip this version",                             "Пропустить эту версию"),
        ["ToastUpdateDownloading"]  = New("Downloading update…",                           "Скачивание обновления…"),
        ["ToastUpdateReady"]        = New("Update ready to install",                       "Обновление готово к установке"),
        ["ToastInstallNow"]         = New("Install now",                                   "Установить сейчас"),
        ["ToastUpdateDownloadFailed"] = New("Update download failed — opening release page", "Не удалось скачать обновление — открываю страницу релиза"),
        ["ToastGetFfmpeg"]          = New("Get FFmpeg in settings",                        "Скачать FFmpeg в настройках"),
        ["WarnSavedAsMp4"]          = New("{0} export needs FFmpeg — saved as MP4 instead ({1}). Install FFmpeg in settings.",
                                          "Для экспорта в {0} нужен FFmpeg — сохранено как MP4 ({1}). Установите FFmpeg в настройках."),
        // Legacy keys kept for compatibility
        ["ToastOpen"]               = New("Open",                                          "Открыть"),
        ["ToastSkip"]               = New("Skip",                                          "Пропустить"),
        ["ToastUpdate"]             = New("Update",                                        "Обновить"),

        // Bitrate label
        ["BitrateMbps"]           = New("{0} Mbps",                                        "{0} Мбит/с"),
        ["BitrateEstimate"]       = New("Est. ~{0} MB per minute",                         "Прибл. ~{0} МБ в минуту"),
    };

    private static Dictionary<string, string> New(string en, string ru)
        => new() { ["en"] = en, ["ru"] = ru };

    public static string Lang { get; private set; } = "en";

    public static void Initialize()
    {
        Lang = Resolve();
        try
        {
            SettingsService.Instance.SettingsChanged -= OnSettingsChanged;
            SettingsService.Instance.SettingsChanged += OnSettingsChanged;
        }
        catch { }
    }

    private static void OnSettingsChanged()
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
