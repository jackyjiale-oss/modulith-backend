using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Web.Common.Localization;

namespace TemplateName.UnitTests.Localization;

public sealed class ApiLocalizationOptionsTests
{
    [Fact]
    public void Only_ui_culture_varies()
    {
        var options = ApiLocalizationExtensions.CreateRequestLocalizationOptions(new ApiLocalizationOptions());

        options.DefaultRequestCulture.Culture.Name.ShouldBe("en");
        options.DefaultRequestCulture.UICulture.Name.ShouldBe("en");
        options.SupportedCultures!.Select(culture => culture.Name).ShouldBe(["en"]);
        options.SupportedUICultures!.Select(culture => culture.Name).ShouldBe(["en", "ms", "zh-Hans"]);
        // The saved locale claim first, so it wins over Accept-Language (decision D7); no query-string or cookie provider.
        options.RequestCultureProviders.Count.ShouldBe(2);
        options.RequestCultureProviders[0].ShouldBeOfType<UserLocaleClaimCultureProvider>();
        options.RequestCultureProviders[1].ShouldBeOfType<AcceptLanguageHeaderRequestCultureProvider>();
        options.FallBackToParentUICultures.ShouldBeTrue();
        options.ApplyCurrentCultureToResponseHeaders.ShouldBeTrue();
    }

    [Fact]
    public void Default_culture_outside_supported_list_fails_validation()
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Localization:DefaultCulture"] = "fr" });

        var exception = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<ApiLocalizationOptions>>().Value);

        exception.Message.ShouldContain("fr");
    }

    [Fact]
    public void Unknown_culture_name_fails_validation()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            ["Localization:SupportedUICultures:0"] = "en",
            ["Localization:SupportedUICultures:1"] = "qq-ZZ",
        });

        var exception = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IOptions<ApiLocalizationOptions>>().Value);

        exception.Message.ShouldContain("qq-ZZ");
    }

    [Fact]
    public void Configured_supported_cultures_replace_the_defaults()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            ["Localization:SupportedUICultures:0"] = "en",
            ["Localization:SupportedUICultures:1"] = "ms",
        });

        services.GetRequiredService<IOptions<ApiLocalizationOptions>>().Value.SupportedUICultures.ShouldBe(["en", "ms"]);
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ServiceCollection().AddLogging().AddApiLocalization(configuration).BuildServiceProvider();
    }
}
