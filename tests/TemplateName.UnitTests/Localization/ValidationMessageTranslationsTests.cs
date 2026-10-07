using System.Globalization;
using System.Text.RegularExpressions;
using FluentValidation.Resources;
using TemplateName.Web.Common.Localization;

namespace TemplateName.UnitTests.Localization;

public sealed partial class ValidationMessageTranslationsTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    public static TheoryData<string> Cultures => new("ms", "zh-Hans");

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Every_translated_validator_has_its_own_text_with_the_english_placeholders(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);
        var translations = new ValidationMessageTranslations();
        var english = new LanguageManager();

        foreach (var key in ValidationMessageTranslations.TranslatedKeys)
        {
            var englishText = english.GetString(key, English);
            var translated = translations.GetString(key, culture);

            englishText.ShouldNotBeNullOrEmpty(key);
            translated.ShouldNotBe(englishText, key);
            Placeholders(translated).ShouldBe(Placeholders(englishText), key);
        }
    }

    [Theory]
    [InlineData("NotEmptyValidator")]
    [InlineData("MaximumLengthValidator")]
    [InlineData("GreaterThanOrEqualValidator")]
    public void Covers_the_validators_the_solution_uses(string key)
        => ValidationMessageTranslations.TranslatedKeys.ShouldContain(key);

    private static string[] Placeholders(string text)
        => [.. PlaceholderPattern().Matches(text).Select(match => match.Value).Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"\{[A-Za-z]+\}")]
    private static partial Regex PlaceholderPattern();
}
