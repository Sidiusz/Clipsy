using Clipsy.Localization;
using Clipsy.Services;
using Microsoft.UI.Xaml.Controls;

namespace Clipsy.Views.Settings;

public sealed partial class SettingsWindow
{
    private void ApplyLocalization()
    {
        NavGeneralLabel.Text  = Strings.Get("TabGeneral");
        NavVideoLabel.Text    = Strings.Get("TabVideo");
        NavOcrLabel.Text      = Strings.Get("TabOcr");
        NavGifLabel.Text      = Strings.Get("TabGif");
        NavHotkeysLabel.Text       = Strings.Get("TabHotkeys");
        NavNotificationsLabel.Text = Strings.Get("TabNotifications");
        NavInfoLabel.Text          = Strings.Get("TabInfo");

        if (TitleBarSubtitle != null) TitleBarSubtitle.Text = Strings.Get("TitleBarSubtitle");
        if (LblTipHeader != null)     LblTipHeader.Text    = Strings.Get("TipLabel");
        if (LblTip != null)           LblTip.Text          = Strings.Get(_tipKeys[_currentTipKeyIndex]);

        HdrGeneral.Text  = Strings.Get("TabGeneral");
        HdrVideo.Text    = Strings.Get("TabVideo");
        HdrOcr.Text      = Strings.Get("TabOcr");
        HdrGif.Text      = Strings.Get("TabGif");
        HdrHotkeys.Text       = Strings.Get("TabHotkeys");
        HdrNotifications.Text = Strings.Get("TabNotifications");
        SubNotifications.Text = Strings.Get("SubNotifications");

        SubGeneral.Text  = Strings.Get("SubGeneral");
        SubVideo.Text    = Strings.Get("SubVideo");
        SubOcr.Text      = Strings.Get("SubOcr");
        SubGif.Text      = Strings.Get("SubGif");

        HelperLanguage.Text  = Strings.Get("HelperLanguage");
        HelperTheme.Text     = Strings.Get("HelperTheme");
        HelperOcr.Text       = Strings.Get("HelperOcr");
        LblTessLang.Text     = Strings.Get("LblTessLang");
        var (tessMin, tessMax) = TessdataService.ApproxSizeRangeMb();
        HelperTessLang.Text  = string.Format(Strings.Get("HelperTessLang"), tessMin, tessMax, Strings.Get("UnitMB"));
        LblTranslateService.Text = Strings.Get("LblTranslateService");
        HelperTranslation.Text   = Strings.Get("HelperTranslation");
        LblTranslateFrom.Text    = Strings.Get("LblTranslateFrom");
        HelperTranslateFrom.Text = Strings.Get("HelperTranslateFrom");
        LblTranslateTo.Text      = Strings.Get("LblTranslateTo");
        HelperTranslateTo.Text   = Strings.Get("HelperTranslateTo");
        BuildTranslateLangDropdowns();
        HelperRemember.Text  = Strings.Get("HelperRemember");
        HelperCodec.Text    = Strings.Get("HelperCodec");
        HelperBitrate.Text  = Strings.Get("HelperBitrate");
        HelperGifColors.Text= Strings.Get("HelperGifColors");
        HelperGifFps.Text   = Strings.Get("HelperGifFps");
        HelperGifDither.Text= Strings.Get("HelperGifDither");

        LblLanguage.Text         = Strings.Get("LblLanguage");
        LblTheme.Text            = Strings.Get("LblTheme");
        LblOcrEngine.Text        = Strings.Get("LblOcrEngine");
        LblScreenshotFolder.Text = Strings.Get("LblScreenshotFolder");
        LblVideoFolder.Text      = Strings.Get("LblVideoFolder");
        LblRememberFolder.Text   = Strings.Get("LblRememberFolder");
        LblAutostart.Text        = Strings.Get("LblAutostart");
        HelperAutostart.Text     = Strings.Get("HelperAutostart");
        LblScreenshotFormat.Text    = Strings.Get("LblScreenshotFormat");
        LblScreenshotCursor.Text    = Strings.Get("LblScreenshotCursor");
        HelperScreenshotCursor.Text = Strings.Get("HelperScreenshotCursor");
        LblVideoFormat.Text         = Strings.Get("LblVideoFormat");
        LblVideoCursor.Text         = Strings.Get("LblVideoCursor");
        HelperVideoCursor.Text      = Strings.Get("HelperVideoCursor");
        LblJpgQuality.Text       = Strings.Get("LblJpgQuality");
        LblAfterSave.Text        = Strings.Get("LblAfterSave");
        HelperAfterSave.Text     = Strings.Get("HelperAfterSave");
        LblUpdates.Text          = Strings.Get("LblUpdates");
        HelperUpdates.Text       = Strings.Get("HelperUpdates");
        LblAppManagement.Text    = Strings.Get("LblAppManagement");
        LblNotifyMaster.Text     = Strings.Get("LblNotifyMaster");
        LblNotifyScreenshot.Text = Strings.Get("LblNotifyScreenshot");
        LblNotifyVideo.Text      = Strings.Get("LblNotifyVideo");
        LblNotifyClipboard.Text  = Strings.Get("LblNotifyClipboard");
        LblNotifyErrors.Text     = Strings.Get("LblNotifyErrors");
        LblNotifyUpdate.Text     = Strings.Get("LblNotifyUpdate");
        LblNotifyHints.Text      = Strings.Get("LblNotifyHints");

        if (LblAuthor != null)        LblAuthor.Text        = Strings.Get("LblAuthorHeader");
        if (LblMit != null)           LblMit.Text           = Strings.Get("LblMit");
        if (LblGithubLine != null)    LblGithubLine.Text    = Strings.Get("LblGithubLine");
        if (LinkGithubOpen != null)   LinkGithubOpen.Content = Strings.Get("BtnOpen");
        if (LblLikeClipsy != null)    LblLikeClipsy.Text    = Strings.Get("LblLikeClipsy");
        if (LblLikeClipsyHint != null) LblLikeClipsyHint.Text = Strings.Get("LblLikeClipsyHint");
        if (LblStarBtn != null)       LblStarBtn.Text       = Strings.Get("BtnStar");
        LangAuto.Content   = Strings.Get("OptAuto");
        LangEn.Content     = Strings.Get("OptEnglish");
        LangRu.Content     = Strings.Get("OptRussian");
        ThemeBtnAutoLabel.Text  = Strings.Get("OptAuto");
        ThemeBtnDarkLabel.Text  = Strings.Get("OptDark");
        ThemeBtnLightLabel.Text = Strings.Get("OptLight");
        var defaultSuffix = " " + Strings.Get("SuffixDefault");
        OcrTesseract.Content = Strings.Get("OptTesseract");
        // WinRT is the OCR engine default — append a localized "(default)" hint.
        OcrWinRt.Content   = Strings.Get("OptWinRtOcr") + defaultSuffix;
        TrSvcMyMemory.Content = Strings.Get("OptMyMemory");
        TrSvcGoogle.Content   = Strings.Get("OptGoogle") + defaultSuffix;
        FmtPng.Content     = Strings.Get("OptPngLossless");
        FmtJpg.Content     = Strings.Get("OptJpgSmaller");
        FmtWebp.Content    = Strings.Get("OptWebpPreview");
        AfterNothing.Content   = Strings.Get("OptDoNothing");
        AfterOpenFile.Content  = Strings.Get("OptOpenFile");
        AfterOpenFolder.Content= Strings.Get("OptOpenFolder");
        UpdHourly.Content  = Strings.Get("OptHourly");
        UpdDaily.Content   = Strings.Get("OptDaily");
        UpdWeekly.Content  = Strings.Get("OptWeekly");
        UpdMonthly.Content = Strings.Get("OptMonthly");
        UpdNever.Content   = Strings.Get("OptNever");

        if (LblSettingsHeader != null) LblSettingsHeader.Text = Strings.Get("SettingsHeader");
        if (LblColAction != null)      LblColAction.Text  = Strings.Get("SectionLabelAction");
        if (LblColBinding != null)     LblColBinding.Text = Strings.Get("SectionLabelBinding");
        HelperVideoFormat.Text = Strings.Get("HelperVideoFormat");
        LblCodecH264Desc.Text  = Strings.Get("CodecH264Desc");
        LblCodecH265Desc.Text  = Strings.Get("CodecH265Desc");
        LblCodecVp9Desc.Text   = Strings.Get("CodecVp9Desc");
        LblCodecAv1Desc.Text   = Strings.Get("CodecAv1Desc");
        LblCodec.Text      = Strings.Get("LblCodec");
        LblResolution.Text = Strings.Get("LblResolution");
        ResBtnOriginal.Content = Strings.Get("OptResNative");
        LblBitrate.Text    = Strings.Get("LblBitrate");
        LblRegionNote.Text = Strings.Get("LblRegionNote");
        LblMicEnabled.Text  = Strings.Get("LblMicEnabled");
        HelperMic.Text      = Strings.Get("HelperMic");
        LblMicDevice.Text   = Strings.Get("LblMicDevice");
        HelperMicDevice.Text = Strings.Get("HelperMicDevice");
        UpdateFfmpegSection();

        LblGifColors.Text  = Strings.Get("LblGifColors");
        LblGifFps.Text     = Strings.Get("LblGifFps");
        LblGifDither.Text = Strings.Get("LblGifDither");

        LblHotkeyHint.Text = Strings.Get("LblHotkeyHint");

        ScreenshotFolderPick.Content = Strings.Get("BtnBrowse");
        VideoFolderPick.Content      = Strings.Get("BtnBrowse");
        BtnCheckNow.Content          = Strings.Get("BtnCheckNow");
        BtnReset.Content             = Strings.Get("BtnReset");
        BtnClose.Content             = Strings.Get("BtnClose");
        BtnSave.Content              = Strings.Get("BtnSave");

        // Rebuild hotkey rows with localized labels (preserves bindings).
        var wasLoading = _loading;
        _loading = true;
        BuildHotkeyRows();

        // WinUI ComboBox caches the SelectedItem's rendered content, so
        // mutating ComboBoxItem.Content above updates dropdown items but
        // leaves the collapsed display showing the old text. Kick each box
        // by toggling SelectedIndex so the ContentPresenter re-renders.
        RefreshComboDisplay(LangBox);
        RefreshComboDisplay(ScreenshotFormatBox);
        RefreshComboDisplay(AfterSaveBox);
        RefreshComboDisplay(UpdateIntervalBox);
        RefreshComboDisplay(VideoFormatBox);
        RefreshComboDisplay(OcrEngineBox);
        RefreshComboDisplay(TranslateServiceBox);
        _loading = wasLoading;
    }

    private static void RefreshComboDisplay(ComboBox? cb)
    {
        if (cb == null) return;
        int idx = cb.SelectedIndex;
        if (idx < 0) return;
        cb.SelectedIndex = -1;
        cb.SelectedIndex = idx;
    }
}
