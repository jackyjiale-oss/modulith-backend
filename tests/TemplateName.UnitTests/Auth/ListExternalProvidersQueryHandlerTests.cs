using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.ExternalLogins.Providers;

namespace TemplateName.UnitTests.Auth;

public sealed class ListExternalProvidersQueryHandlerTests
{
    private const string Providers = "Auth:External:Providers";
    private const string Secret = "facebook-app-secret-value";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_only_enabled_providers_in_name_order_with_their_type_and_nothing_configured()
    {
        var catalog = Catalog(new Dictionary<string, string?>
        {
            [$"{Providers}:Google:Enabled"] = "true",
            [$"{Providers}:Google:ClientIds:0"] = "web.apps.example",
            [$"{Providers}:Facebook:Enabled"] = "true",
            [$"{Providers}:Facebook:AppId"] = "1234567890",
            [$"{Providers}:Facebook:AppSecret"] = Secret,
            [$"{Providers}:Okta:Enabled"] = "true",
            [$"{Providers}:Okta:Type"] = "oidc",
            [$"{Providers}:Okta:DisplayName"] = "Sign in with Okta",
            [$"{Providers}:Okta:Authority"] = "https://example.okta.com",
            [$"{Providers}:Okta:ClientIds:0"] = "okta-client",
            [$"{Providers}:Apple:Enabled"] = "false",
            [$"{Providers}:Apple:ClientIds:0"] = "com.example.web",
        });

        var result = await new ListExternalProvidersQueryHandler(catalog).HandleAsync(new ListExternalProvidersQuery(), Ct);

        result.Value.ShouldBe(
        [
            new ExternalProviderResponse("facebook", "Facebook", "facebook"),
            new ExternalProviderResponse("google", "Google", "oidc"),
            new ExternalProviderResponse("okta", "Sign in with Okta", "oidc"),
        ]);
        result.Value.ShouldAllBe(provider => provider.ToString() == nameof(ExternalProviderResponse));
    }

    [Fact]
    public async Task No_enabled_provider_is_an_empty_list()
    {
        var result = await new ListExternalProvidersQueryHandler(Catalog([])).HandleAsync(new ListExternalProvidersQuery(), Ct);

        result.Value.ShouldBeEmpty();
    }

    private static IExternalProviderCatalog Catalog(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        AuthModule.AddExternalLogin(services, configuration);
        return services.BuildServiceProvider().GetRequiredService<IExternalProviderCatalog>();
    }
}
