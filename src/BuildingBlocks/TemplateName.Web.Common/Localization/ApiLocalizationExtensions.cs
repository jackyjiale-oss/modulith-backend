using System.Globalization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TemplateName.Web.Common.Localization;

public static class ApiLocalizationExtensions
{
    /// <summary>
    /// The request localization the API uses: the UI culture comes from the signed-in user's saved <c>locale</c> claim
    /// (<see cref="UserLocaleClaimCultureProvider"/>), else from <c>Accept-Language</c> (regions fall back to their parent, so
    /// <c>zh-CN</c> gets <c>zh-Hans</c>; an unsupported claim falls through to the header; anything unsupported or malformed gets
    /// <see cref="ApiLocalizationOptions.DefaultCulture"/>), the formatting culture is always the default culture, and the chosen
    /// language is echoed in <c>Content-Language</c>.
    /// </summary>
    public static RequestLocalizationOptions CreateRequestLocalizationOptions(ApiLocalizationOptions settings)
        => Apply(new RequestLocalizationOptions(), settings);

    /// <summary>
    /// Binds and validates the <c>Localization</c> section (on start), configures request localization from it, and switches
    /// FluentValidation's process-wide language manager to one that also speaks every supported language (ADR 0009).
    /// </summary>
    public static IServiceCollection AddApiLocalization(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(ApiLocalizationOptions.SectionName);

        services.AddOptions<ApiLocalizationOptions>()
            .Bind(section)
            .Configure(options =>
            {
                // The binder appends configured array items to the defaults; a configured list replaces them instead.
                var configured = section.GetSection(nameof(ApiLocalizationOptions.SupportedUICultures)).Get<string[]>();
                if (configured is { Length: > 0 })
                {
                    options.SupportedUICultures = configured;
                }
            })
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RequestLocalizationOptions>().Configure<IOptions<ApiLocalizationOptions>>(
            (options, settings) => Apply(options, settings.Value));

        // FluentValidation reads its messages from one static language manager; every host sets the same translations.
        ValidatorOptions.Global.LanguageManager = new ValidationMessageTranslations();

        return services;
    }

    /// <summary>
    /// Makes background work (no request culture) use the default culture instead of the server's regional settings, then adds the
    /// request localization middleware. Register it after <c>UseAuthentication</c>, so the saved <c>locale</c> claim is visible, and
    /// before <c>UseExceptionHandler</c>, so error responses are localized too.
    /// </summary>
    public static WebApplication UseApiLocalization(this WebApplication app)
    {
        var defaultCulture = CultureInfo.GetCultureInfo(app.Services.GetRequiredService<IOptions<ApiLocalizationOptions>>().Value.DefaultCulture);
        CultureInfo.DefaultThreadCurrentCulture = defaultCulture;
        CultureInfo.DefaultThreadCurrentUICulture = defaultCulture;

        app.UseRequestLocalization();
        return app;
    }

    private static RequestLocalizationOptions Apply(RequestLocalizationOptions options, ApiLocalizationOptions settings)
    {
        options.DefaultRequestCulture = new RequestCulture(settings.DefaultCulture);
        options.SupportedCultures = [CultureInfo.GetCultureInfo(settings.DefaultCulture)];
        options.SupportedUICultures = [.. settings.SupportedUICultures.Select(CultureInfo.GetCultureInfo)];
        // The saved locale claim first, so it wins over Accept-Language (decision D7); no query-string or cookie provider.
        options.RequestCultureProviders = [new UserLocaleClaimCultureProvider(), new AcceptLanguageHeaderRequestCultureProvider()];
        options.FallBackToParentUICultures = true;
        options.ApplyCurrentCultureToResponseHeaders = true;
        return options;
    }
}
