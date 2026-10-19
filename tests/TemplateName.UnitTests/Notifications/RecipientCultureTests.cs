using System.Globalization;
using TemplateName.Modules.Notifications.Application.Catalog;

namespace TemplateName.UnitTests.Notifications;

public sealed class RecipientCultureTests
{
    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("   ", "en")]
    [InlineData("!!", "en")]
    [InlineData("fr-FR", "en")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("ms-MY", "ms")]
    [InlineData("ms", "ms")]
    [InlineData("en-GB", "en")]
    [InlineData("en", "en")]
    [InlineData("zh-Hant", "en")]
    [InlineData("zh-TW", "en")]
    [InlineData("MS-my", "ms")]
    public void Resolve_maps_locales_to_supported_cultures(string? locale, string expected)
    {
        RecipientCulture.Resolve(locale).ShouldBe(expected);
    }

    [Fact]
    public void Resolve_ignores_current_ui_culture()
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ms");

            RecipientCulture.Resolve(null).ShouldBe("en");
            RecipientCulture.Resolve("fr-FR").ShouldBe("en");
            RecipientCulture.Resolve("zh-CN").ShouldBe("zh-Hans");
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Supported_cultures_are_en_ms_and_zh_Hans()
    {
        RecipientCulture.Supported.ShouldBe(["en", "ms", "zh-Hans"]);
    }
}
