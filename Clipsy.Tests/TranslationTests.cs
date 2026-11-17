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

    [Fact]
    public async Task TimedOutProviderFallsBackAndReportsProgress()
    {
        var attempted = new List<string>();
        var progress = new List<string>();
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
                return Task.FromResult(new TranslationResult("Привет"));
            }, TimeSpan.FromMilliseconds(30), progress: new InlineProgress(progress.Add));

        Assert.Equal("Привет", result.Text);
        Assert.Equal(new[] { "Bing", "Google" }, attempted);
        Assert.Equal(attempted, progress);
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
    public async Task EveryProviderTimingOutReturnsAnError()
    {
        var result = await TranslationService.TranslateAsync("Hello", "en", "ru", "Bing",
            (_, _, _, _, _) => new TaskCompletionSource<TranslationResult>().Task,
            TimeSpan.FromMilliseconds(20));
        Assert.Null(result.Text);
        Assert.Equal("TranslateNetwork", result.ErrorKey);
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

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
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
