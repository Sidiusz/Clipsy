using System.Linq;
using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

public class TranslationTests
{
    [Fact]
    public void ManualAlternativesPreferGoogleAndExcludeSelectedService()
    {
        Assert.Equal(new[] { "Google", "MyMemory" }, TranslationService.Alternatives("Bing").ToArray());
        Assert.Equal(new[] { "Bing", "MyMemory" }, TranslationService.Alternatives("Google").ToArray());
        Assert.Equal("Google", TranslationService.NormalizeService("unknown"));
    }

    [Fact]
    public void ChineseCodesMapToBingScripts()
    {
        Assert.Equal("zh-Hans", BingTranslator.ToBingCode("zh-CN"));
        Assert.Equal("ru", BingTranslator.ToBingCode("ru"));
    }

    [Fact]
    public async Task TimedOutProviderReturnsItsErrorWithoutContactingOthers()
    {
        var attempted = new List<string>();
        CancellationToken firstToken = default;
        var result = await TranslationService.TranslateAsync("Hello", "en", "ru", "Bing",
            (service, _, _, _, ct) =>
            {
                attempted.Add(service);
                if (service == "Bing")
                {
                    firstToken = ct;
                    return new TaskCompletionSource<TranslationResult>().Task;
                }
                throw new Exception("Another provider must not receive the text");
            }, TimeSpan.FromMilliseconds(30));

        Assert.Null(result.Text);
        Assert.Equal("TranslateTimeout", result.ErrorKey);
        Assert.Equal(new[] { "Bing" }, attempted);
        Assert.True(firstToken.IsCancellationRequested);
    }

    [Fact]
    public async Task CallerCancellationDoesNotSendTextToAnotherProvider()
    {
        using var cancel = new CancellationTokenSource();
        var attempted = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TranslationService.TranslateAsync(
            "Hello", "en", "ru", "Bing", (service, _, _, _, ct) =>
            {
                attempted.Add(service);
                cancel.Cancel();
                return Task.FromCanceled<TranslationResult>(ct);
            }, TimeSpan.FromSeconds(1), cancel.Token));
        Assert.Equal(new[] { "Bing" }, attempted);
    }

    [Fact]
    public async Task ProviderFailureDoesNotAutomaticallySwitchOrSendMoreChunks()
    {
        var attempted = new List<string>();
        var result = await TranslationService.TranslateAsync(new string('a', 2200), "en", "ru", "Bing",
            (service, _, _, _, _) =>
            {
                attempted.Add(service);
                return Task.FromResult(new TranslationResult(null, "TranslateQuota"));
            }, TimeSpan.FromSeconds(1));
        Assert.Null(result.Text);
        Assert.Equal("TranslateQuota", result.ErrorKey);
        Assert.Equal(new[] { "Bing" }, attempted);
    }

    [Theory]
    [InlineData("en", "ru", "ru", "Hello", "ru", "en")]
    [InlineData("auto", "ui", "ru", "Hello", "ru", "en")]
    [InlineData("auto", "ui", "en", "Привет", "en", "ru")]
    [InlineData("auto", "ui", "ru", "Привет", "en", "ru")]
    public void SwapResolvesAutomaticAndUiLanguages(string from, string to, string ui, string text,
        string expectedFrom, string expectedTo)
    {
        Assert.Equal((expectedFrom, expectedTo), TranslationService.SwapLanguages(from, to, ui, text, null));
    }

    [Fact]
    public void SwapUsesTheCompletedTranslationDirection()
    {
        var result = new TranslationResult("Hello", SourceLanguage: "ru", TargetLanguage: "en");
        Assert.Equal(("en", "ru"), TranslationService.SwapLanguages("auto", "ui", "ru", "Привет", result));
    }

    [Fact]
    public async Task ProviderDetectionDoesNotReplaceTheExplicitLanguagePair()
    {
        var result = await TranslationService.TranslateAsync("Hello", "ru", "en", "Google",
            (_, _, _, _, _) => Task.FromResult(new TranslationResult("Hello", SourceLanguage: "en")),
            TimeSpan.FromSeconds(1));
        Assert.Equal("ru", result.SourceLanguage);
        Assert.Equal("en", result.TargetLanguage);
    }

    [Fact]
    public async Task ExplicitLanguagePairIsNotSilentlyReversed()
    {
        var result = await TranslationService.TranslateAsync("Hello", "en", "en", "Bing",
            (_, _, from, to, _) =>
            {
                Assert.Equal("en", from);
                Assert.Equal("en", to);
                return Task.FromResult(new TranslationResult("Hello"));
            }, TimeSpan.FromSeconds(1));
        Assert.Equal("en", result.TargetLanguage);
    }

    // Hits the network; runs only with CLIPSY_LIVE=1.
    [Fact]
    public async System.Threading.Tasks.Task LiveDefaultServiceTranslatesBothWays()
    {
        if (System.Environment.GetEnvironmentVariable("CLIPSY_LIVE") != "1") return;
        var ru = await TranslationService.TranslateAsync("Screenshot copied to clipboard.", "auto", "ru");
        Assert.Contains("буфер", ru.Text);
        var en = await TranslationService.TranslateAsync("Скриншот скопирован в буфер обмена.", "auto", "en");
        Assert.Contains("clipboard", en.Text);
    }
}
