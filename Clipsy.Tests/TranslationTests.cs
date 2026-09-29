using System.Linq;
using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

public class TranslationTests
{
    [Fact]
    public void FallbackStartsWithChosenServiceAndTriesEveryOther()
    {
        Assert.Equal(new[] { "Google", "Bing", "MyMemory" }, TranslationService.FallbackOrder("Google").ToArray());
        Assert.Equal(new[] { "Bing", "Google", "MyMemory" }, TranslationService.FallbackOrder("unknown").ToArray());
    }

    [Fact]
    public void ChineseCodesMapToBingScripts()
    {
        Assert.Equal("zh-Hans", BingTranslator.ToBingCode("zh-CN"));
        Assert.Equal("ru", BingTranslator.ToBingCode("ru"));
    }

    // Hits the network; runs only with CLIPSY_LIVE=1.
    [Fact]
    public async System.Threading.Tasks.Task LiveBingTranslatesBothWays()
    {
        if (System.Environment.GetEnvironmentVariable("CLIPSY_LIVE") != "1") return;
        var ru = await TranslationService.TranslateAsync("Screenshot copied to clipboard.", "auto", "ru", "Bing");
        Assert.Contains("буфер", ru.Text);
        var en = await TranslationService.TranslateAsync("Скриншот скопирован в буфер обмена.", "auto", "en", "Bing");
        Assert.Contains("clipboard", en.Text);
    }
}
