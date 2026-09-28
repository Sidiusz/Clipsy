using System.IO;
using System.Text;
using Clipsy.Services;
using Windows.Foundation;
using Xunit;

namespace Clipsy.Tests;

public class TextServicesTests
{
    private static OcrWord W(string t, double x, double y, double w = 20, double h = 12) => new(t, new Rect(x, y, w, h));

    [Fact]
    public void LayoutJoinsCjkWithoutSpaces()
    {
        var words = new[] { W("你", 0, 0), W("好", 22, 0), W("world", 50, 0, 40) };
        Assert.Equal("你好 world", OcrTextLayout.BuildText(words));
    }

    [Fact]
    public void LayoutOrdersArabicRightToLeft()
    {
        var words = new[] { W("بالعالم", 0, 0, 40), W("مرحبا", 50, 0, 40) };
        Assert.Equal("مرحبا بالعالم", OcrTextLayout.BuildText(words));
    }

    [Fact]
    public void LayoutKeepsMixedHeightWordsOnOneLine()
    {
        // Lowercase word sits lower than a capitalized one on the same baseline.
        var words = new[] { W("PP-OCRv5", 0, 10, 60, 12), W("в", 65, 14, 8, 8), W("тесте", 76, 14, 40, 8), W("next", 0, 40, 30, 12) };
        Assert.Equal("PP-OCRv5 в тесте\nnext", OcrTextLayout.BuildText(words).Replace("\r", ""));
    }

    [Theory]
    [InlineData("Привет, как дела?", "ru")]
    [InlineData("Привіт, як справи?", "uk")]
    [InlineData("Größe und Übung", "de")]
    [InlineData("你好世界", "zh-CN")]
    [InlineData("こんにちは", "ja")]
    [InlineData("안녕하세요", "ko")]
    [InlineData("مرحبا", "ar")]
    [InlineData("Hello there", "en")]
    public void DetectsLanguageByScript(string text, string expected)
        => Assert.Equal(expected, TranslationService.DetectLanguage(text, out _));

    [Fact]
    public void ChunksRespectByteLimitAndRestoreSeparators()
    {
        var text = "Первая строка.\n" + string.Concat(Enumerable.Repeat("Длинное предложение номер один. ", 20)).TrimEnd();
        var chunks = TranslationService.PackChunks(text, s => Encoding.UTF8.GetByteCount(s), 480);
        Assert.All(chunks, c => Assert.True(Encoding.UTF8.GetByteCount(c.Chunk) <= 480));
        var rebuilt = string.Concat(chunks.Select(c => c.Chunk + c.Separator)).TrimEnd();
        Assert.Equal(text.Replace(" ", ""), rebuilt.Replace(" ", ""));
        Assert.Equal(2, rebuilt.Split('\n').Length); // long line split with spaces, not newlines
    }

    [Fact]
    public async Task GitBlobHashMatchesGit()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "hello\n");
            // `echo hello | git hash-object --stdin`
            Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", await TessdataService.GitBlobSha1Async(path, default));
        }
        finally { File.Delete(path); }
    }
}
