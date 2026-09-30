using Sidey.Core.Localization;

namespace Sidey.Core.Tests;

[CollectionDefinition(nameof(SharedLocalizationCollection), DisableParallelization = true)]
public sealed class SharedLocalizationCollection;

[Collection(nameof(SharedLocalizationCollection))]
public sealed class I18nTests
{
    [Fact]
    public void LoadsNestedKoreanCatalogByDottedKey()
    {
        const string Key = "onboarding.tagline";

        string value = I18n.Get(Key);

        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.NotEqual(Key, value);
    }

    [Theory]
    [InlineData("ko", "ko-KR")]
    [InlineData("en-GB", "en-US")]
    [InlineData("ja", "ja-JP")]
    [InlineData("zh-Hans-SG", "zh-CN")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-Hant", "zh-TW")]
    [InlineData("uk", "uk-UA")]
    [InlineData("ru-KZ", "ru-RU")]
    [InlineData("it-CH", "it-IT")]
    [InlineData("pt-PT", "pt-PT")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("es-MX", "es-ES")]
    [InlineData("cs", "cs-CZ")]
    [InlineData("tr", "tr-TR")]
    [InlineData("ro", "ro-RO")]
    [InlineData("bg", "bg-BG")]
    [InlineData("sr-Cyrl", "sr-Cyrl-RS")]
    [InlineData("sr-Latn-BA", "sr-Latn-RS")]
    [InlineData("sr-RS", "sr-Cyrl-RS")]
    [InlineData("pl", "pl-PL")]
    [InlineData("nl-BE", "nl-BE")]
    [InlineData("nl", "nl-NL")]
    [InlineData("fr-CA", "fr-FR")]
    [InlineData("he", "he-IL")]
    [InlineData("iw-IL", "he-IL")]
    [InlineData("de-AT", "de-DE")]
    [InlineData("sv-SE", "ko-KR")]
    public void NormalizesSystemUiLanguageToASupportedCatalog(string requested, string expected)
    {
        Assert.Equal(expected, I18n.NormalizeLanguage(requested));
    }

    [Fact]
    public void SupportedCatalogOrderMatchesTheLanguagePicker()
    {
        Assert.Equal(
            [
                "ko-KR", "en-US", "ja-JP", "zh-CN", "zh-TW", "uk-UA", "ru-RU",
                "it-IT", "pt-PT", "es-ES", "cs-CZ", "tr-TR", "ro-RO", "bg-BG", "pt-BR",
                "sr-Cyrl-RS", "pl-PL", "sr-Latn-RS", "nl-BE", "fr-FR", "nl-NL", "he-IL",
                "de-DE",
            ],
            I18n.SupportedLanguages);
    }

    [Fact]
    public void HebrewUsesRightToLeftLayoutAndOtherSupportedLanguagesUseLeftToRight()
    {
        string previous = I18n.Language;
        try
        {
            I18n.SetLanguage("he-IL");
            Assert.True(I18n.IsRightToLeft);

            foreach (string language in I18n.SupportedLanguages.Where(value => value != "he-IL"))
            {
                I18n.SetLanguage(language);
                Assert.False(I18n.IsRightToLeft, language);
            }
        }
        finally
        {
            I18n.SetLanguage(previous);
        }
    }

    [Fact]
    public void FormatsCatalogValuesAndReturnsMissingKeysSafely()
    {
        string formatted = I18n.Format("groups.delete.confirmation.title", "테스트");
        Assert.NotEqual("groups.delete.confirmation.title", formatted);
        Assert.Contains("테스트", formatted, StringComparison.Ordinal);
        Assert.NotEqual("renderer_metrics.samples.empty", I18n.Get("renderer_metrics.samples.empty"));
        Assert.Equal("missing.example", I18n.Get("missing.example"));
    }

}
