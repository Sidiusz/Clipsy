using Clipsy.Services;
using Xunit;

namespace Clipsy.Tests;

public class OcrEngineMigrationTests
{
    [Theory]
    [InlineData("eng", "en")]
    [InlineData("rus", "eslav")]
    [InlineData("ukr", "eslav")]
    [InlineData("deu", "latin")]
    [InlineData("jpn", "ch")]
    [InlineData("kor", "korean")]
    [InlineData("ara", "arabic")]
    public void TesseractLanguagesMapToPpModels(string tess, string pp)
        => Assert.Equal(pp, OcrEngineMigration.ModelKeyForTesseract(tess));

    [Fact]
    public void EveryMappedModelExists()
    {
        foreach (var code in new[] { "eng", "rus", "bul", "chi_sim", "kor", "ara", "tha", "ell", "hin", "tam", "tel", "deu" })
        {
            var key = OcrEngineMigration.ModelKeyForTesseract(code);
            Assert.Contains(PpOcrV5Service.RecognizerModels, m => m.Key == key);
        }
    }

    [Fact]
    public void NewSettingsDefaultToPpOcr()
        => Assert.Equal("PPOCRv5", new AppSettings().OcrEngine);
}
