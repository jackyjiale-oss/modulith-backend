using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TemplateName.Web.Common.Localization;

namespace TemplateName.UnitTests.Web;

/// <summary>
/// Runs the real <see cref="RequestLocalizationMiddleware"/> with the API's options, so the fall-through to <c>Accept-Language</c> is
/// the framework's behaviour, not an assumption about it.
/// </summary>
public sealed class UserLocaleClaimCultureProviderTests
{
    [Fact]
    public async Task Claim_locale_wins_over_accept_language()
    {
        var culture = await ResolveAsync(locale: "ms", acceptLanguage: "zh-Hans");

        culture.UICulture.Name.ShouldBe("ms");
        culture.Culture.Name.ShouldBe("en");
        culture.Provider.ShouldBeOfType<UserLocaleClaimCultureProvider>();
    }

    [Fact]
    public async Task Unsupported_claim_locale_falls_through_to_accept_language()
    {
        var culture = await ResolveAsync(locale: "fr-FR", acceptLanguage: "zh-Hans");

        culture.UICulture.Name.ShouldBe("zh-Hans");
        culture.Culture.Name.ShouldBe("en");
        culture.Provider.ShouldBeOfType<AcceptLanguageHeaderRequestCultureProvider>();
    }

    [Fact]
    public async Task Malformed_claim_locale_falls_through_to_accept_language()
    {
        var culture = await ResolveAsync(locale: "!!!;q=abc", acceptLanguage: "ms");

        culture.UICulture.Name.ShouldBe("ms");
    }

    [Fact]
    public async Task Region_claim_locale_falls_back_to_its_parent()
    {
        var culture = await ResolveAsync(locale: "zh-CN", acceptLanguage: "ms");

        culture.UICulture.Name.ShouldBe("zh-Hans");
        culture.Culture.Name.ShouldBe("en");
    }

    [Fact]
    public async Task Without_a_claim_accept_language_decides()
    {
        var culture = await ResolveAsync(locale: null, acceptLanguage: "ms");

        culture.UICulture.Name.ShouldBe("ms");
        culture.Provider.ShouldBeOfType<AcceptLanguageHeaderRequestCultureProvider>();
    }

    [Fact]
    public async Task Unsupported_claim_and_malformed_accept_language_get_the_default_culture()
    {
        var culture = await ResolveAsync(locale: "fr", acceptLanguage: "!!!;q=abc");

        culture.UICulture.Name.ShouldBe("en");
        culture.Culture.Name.ShouldBe("en");
    }

    [Fact]
    public async Task Claim_of_an_unauthenticated_identity_is_ignored()
    {
        var culture = await ResolveAsync(locale: "ms", acceptLanguage: "zh-Hans", isAuthenticated: false);

        culture.UICulture.Name.ShouldBe("zh-Hans");
    }

    private static async Task<(CultureInfo Culture, CultureInfo UICulture, IRequestCultureProvider? Provider)> ResolveAsync(
        string? locale,
        string acceptLanguage,
        bool isAuthenticated = true)
    {
        var options = ApiLocalizationExtensions.CreateRequestLocalizationOptions(new ApiLocalizationOptions());
        IRequestCultureFeature? feature = null;
        var middleware = new RequestLocalizationMiddleware(
            context =>
            {
                feature = context.Features.Get<IRequestCultureFeature>();
                return Task.CompletedTask;
            },
            Options.Create(options),
            NullLoggerFactory.Instance);

        var claims = locale is null ? [] : new[] { new Claim("locale", locale) };
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, isAuthenticated ? "Bearer" : null)),
        };
        context.Request.Headers.AcceptLanguage = acceptLanguage;

        await middleware.Invoke(context);

        feature.ShouldNotBeNull();
        return (feature.RequestCulture.Culture, feature.RequestCulture.UICulture, feature.Provider);
    }
}
