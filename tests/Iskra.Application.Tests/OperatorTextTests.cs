using Iskra.Application.Localization;
using Iskra.Core;

namespace Iskra.Application.Tests;

public class OperatorTextTests
{
    private static readonly string[] Languages =
        [IskraLanguages.Ukrainian, IskraLanguages.English, IskraLanguages.German];

    [Fact]
    public void All_languages_have_the_same_error_codes()
    {
        var expected = OperatorText.ErrorCodes(IskraLanguages.Ukrainian).Order().ToArray();

        Assert.Equal(24, expected.Length);
        Assert.Contains("E_BATCH_REQUIRED", expected);
        Assert.Contains("E_RELEASE_REVOKED", expected);

        foreach (var language in Languages)
            Assert.Equal(expected, OperatorText.ErrorCodes(language).Order().ToArray());
    }

    [Theory]
    [InlineData("uk", "Введіть ID партії")]
    [InlineData("en", "Enter a batch ID")]
    [InlineData("de", "Geben Sie eine Chargen-ID")]
    public void Batch_required_hint_is_available_in_every_language(string language, string expected)
    {
        Assert.Contains(expected, OperatorText.ErrorHint("E_BATCH_REQUIRED", language));
    }

    [Theory]
    [InlineData("uk", "Невідома помилка")]
    [InlineData("en", "Unknown error")]
    [InlineData("de", "Unbekannter Fehler")]
    public void Unknown_error_fallback_is_localized(string language, string expected)
    {
        Assert.Contains(expected, OperatorText.ErrorHint("E_FUTURE", language));
    }
}
