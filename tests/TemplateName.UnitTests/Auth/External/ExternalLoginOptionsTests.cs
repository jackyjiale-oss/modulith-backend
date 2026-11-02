using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Infrastructure.External;

namespace TemplateName.UnitTests.Auth.External;

public sealed class ExternalLoginOptionsTests
{
    private const string Section = "Auth:External";
    private const string Providers = Section + ":Providers";

    [Fact]
    public void Defaults_match_the_spec_and_start_with_no_provider()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        var options = services.GetRequiredService<IOptions<ExternalLoginOptions>>().Value;
        ExternalLoginOptions.SectionName.ShouldBe(Section);
        options.AllowSignUp.ShouldBeTrue();
        options.MaxTokenAge.ShouldBe(TimeSpan.FromMinutes(5));
        options.Providers.ShouldBeEmpty();
        services.GetRequiredService<IExternalProviderCatalog>().Enabled.ShouldBeEmpty();
        services.GetRequiredService<IExternalProviderCatalog>().AllowSignUp.ShouldBeTrue();
    }

    [Fact]
    public void The_catalog_reports_sign_up_switched_off()
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"{Section}:AllowSignUp"] = "false" });

        services.GetRequiredService<IExternalProviderCatalog>().AllowSignUp.ShouldBeFalse();
    }

    [Fact]
    public void Disabled_providers_are_never_validated_or_listed()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            // Each would fail if it were enabled: no client IDs, no app secret, an unknown name without a type, a bad authority.
            [$"{Providers}:Google:Enabled"] = "false",
            [$"{Providers}:Facebook:Enabled"] = "false",
            [$"{Providers}:Facebook:AppId"] = "123",
            [$"{Providers}:Okta:Enabled"] = "false",
            [$"{Providers}:Okta:Authority"] = "http://insecure.example.com",
            [$"{Providers}:Bad name!:Enabled"] = "false",
        });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        var catalog = services.GetRequiredService<IExternalProviderCatalog>();
        catalog.Enabled.ShouldBeEmpty();
        catalog.Find("google").ShouldBeNull();
        catalog.Find("facebook").ShouldBeNull();
        catalog.Find("okta").ShouldBeNull();
    }

    [Theory]
    [InlineData("Google")]
    [InlineData("Microsoft")]
    [InlineData("Apple")]
    public void An_enabled_provider_without_client_ids_fails_on_start_and_names_the_key(string provider)
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"{Providers}:{provider}:Enabled"] = "true" });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.OptionsType.ShouldBe(typeof(ExternalLoginOptions));
        failure.Message.ShouldContain($"Auth:External:Providers:{provider}:ClientIds");
    }

    [Fact]
    public void A_blank_client_id_fails_on_start_and_names_the_entry()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            [$"{Providers}:Google:Enabled"] = "true",
            [$"{Providers}:Google:ClientIds:0"] = "google-client-id.apps.example",
            [$"{Providers}:Google:ClientIds:1"] = "   ",
        });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("Auth:External:Providers:Google:ClientIds:1");
        failure.Message.ShouldNotContain("google-client-id.apps.example");
    }

    [Fact]
    public void An_enabled_provider_with_client_ids_starts()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            [$"{Providers}:Google:Enabled"] = "true",
            [$"{Providers}:Google:ClientIds:0"] = "web.apps.example",
            [$"{Providers}:Google:ClientIds:1"] = "ios.apps.example",
        });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Theory]
    [InlineData("AppId")]
    [InlineData("AppSecret")]
    public void Facebook_without_an_app_id_or_secret_fails_on_start_and_names_the_key(string missing)
    {
        var settings = new Dictionary<string, string?>
        {
            [$"{Providers}:Facebook:Enabled"] = "true",
            [$"{Providers}:Facebook:AppId"] = "1234567890",
            [$"{Providers}:Facebook:AppSecret"] = "the-facebook-secret",
        };
        settings.Remove($"{Providers}:Facebook:{missing}");
        using var services = BuildServices(settings);

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain($"Auth:External:Providers:Facebook:{missing}");
    }

    [Fact]
    public void Facebook_with_an_app_id_and_secret_starts_without_client_ids()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            [$"{Providers}:Facebook:Enabled"] = "true",
            [$"{Providers}:Facebook:AppId"] = "1234567890",
            [$"{Providers}:Facebook:AppSecret"] = "the-facebook-secret",
        });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void A_name_other_than_the_four_without_the_oidc_type_fails_on_start()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            [$"{Providers}:Okta:Enabled"] = "true",
            [$"{Providers}:Okta:Authority"] = "https://example.okta.com",
            [$"{Providers}:Okta:ClientIds:0"] = "okta-client",
        });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("Auth:External:Providers:Okta:Type");
    }

    [Fact]
    public void An_oidc_entry_with_an_https_authority_and_client_ids_starts()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            [$"{Providers}:Okta:Enabled"] = "true",
            [$"{Providers}:Okta:Type"] = "OIDC",
            [$"{Providers}:Okta:Authority"] = "https://example.okta.com/oauth2/default",
            [$"{Providers}:Okta:ClientIds:0"] = "okta-client",
        });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Theory]
    [InlineData("http://example.okta.com")]
    [InlineData("/oauth2/default")]
    [InlineData("example.okta.com")]
    [InlineData("https://example.okta.com/path?x=1")]
    [InlineData("https://example.okta.com/path#fragment")]
    [InlineData("https://user:pass@example.okta.com")]
    [InlineData("")]
    public void An_oidc_entry_whose_authority_is_not_an_absolute_https_url_fails_on_start(string authority)
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            [$"{Providers}:Okta:Enabled"] = "true",
            [$"{Providers}:Okta:Type"] = "oidc",
            [$"{Providers}:Okta:Authority"] = authority,
            [$"{Providers}:Okta:ClientIds:0"] = "okta-client",
        });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("Auth:External:Providers:Okta:Authority");
        if (authority.Length > 0)
        {
            failure.Message.ShouldNotContain(authority);
        }
    }

    [Fact]
    public void A_type_that_is_not_the_providers_own_fails_on_start()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            [$"{Providers}:Google:Enabled"] = "true",
            [$"{Providers}:Google:Type"] = "saml",
            [$"{Providers}:Google:ClientIds:0"] = "web.apps.example",
        });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("Auth:External:Providers:Google:Type");
    }

    [Theory]
    [InlineData("Bad name")]
    [InlineData("a/b")]
    [InlineData("-leading")]
    public void A_provider_name_that_cannot_be_a_route_segment_fails_on_start(string name)
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            [$"{Providers}:{name}:Enabled"] = "true",
            [$"{Providers}:{name}:Type"] = "oidc",
            [$"{Providers}:{name}:Authority"] = "https://example.okta.com",
            [$"{Providers}:{name}:ClientIds:0"] = "okta-client",
        });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain($"Auth:External:Providers:{name}");
    }

    [Fact]
    public void Two_entries_that_differ_only_by_case_or_spaces_fail_on_start()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            [$"{Providers}:Google:Enabled"] = "true",
            [$"{Providers}:Google:ClientIds:0"] = "a.apps.example",
            [$"{Providers}: google:Enabled"] = "true",
            [$"{Providers}: google:ClientIds:0"] = "b.apps.example",
        });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("same provider name");
    }

    [Theory]
    [InlineData("common")]
    [InlineData("organizations")]
    [InlineData("consumers")]
    [InlineData("8a2b1c3d-0000-4000-8000-0123456789ab")]
    public void A_microsoft_tenant_that_is_a_keyword_or_a_guid_starts(string tenant)
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            [$"{Providers}:Microsoft:Enabled"] = "true",
            [$"{Providers}:Microsoft:TenantId"] = tenant,
            [$"{Providers}:Microsoft:ClientIds:0"] = "ms-client",
        });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void A_microsoft_tenant_that_is_neither_fails_on_start_without_quoting_it()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            [$"{Providers}:Microsoft:Enabled"] = "true",
            [$"{Providers}:Microsoft:TenantId"] = "contoso.onmicrosoft.com",
            [$"{Providers}:Microsoft:ClientIds:0"] = "ms-client",
        });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("Auth:External:Providers:Microsoft:TenantId");
        failure.Message.ShouldNotContain("contoso");
    }

    [Theory]
    [InlineData("00:01:00")]
    [InlineData("00:05:00")]
    [InlineData("01:00:00")]
    public void Max_token_age_at_the_ends_of_its_range_starts(string value)
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"{Section}:MaxTokenAge"] = value });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Theory]
    [InlineData("00:00:30")]
    [InlineData("00:00:59")]
    [InlineData("01:00:01")]
    [InlineData("02:00:00")]
    public void Max_token_age_outside_its_range_fails_on_start(string value)
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"{Section}:MaxTokenAge"] = value });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.OptionsType.ShouldBe(typeof(ExternalLoginOptions));
        failure.Message.ShouldContain("MaxTokenAge");
    }

    [Fact]
    public void A_failure_message_never_contains_a_configured_secret_or_client_id()
    {
        const string Secret = "tOp-s3cret-app-secret";
        const string ClientId = "1234-client-id.apps.example";
        using var services = BuildServices(new Dictionary<string, string?>
        {
            // Facebook lacks AppId and the generic entry has a bad authority, while both carry values that must not be echoed.
            [$"{Providers}:Facebook:Enabled"] = "true",
            [$"{Providers}:Facebook:AppSecret"] = Secret,
            [$"{Providers}:Facebook:ClientIds:0"] = ClientId,
            [$"{Providers}:Okta:Enabled"] = "true",
            [$"{Providers}:Okta:Type"] = "oidc",
            [$"{Providers}:Okta:Authority"] = "http://insecure.example.com",
            [$"{Providers}:Okta:ClientIds:0"] = ClientId,
            [$"{Providers}:Google:Enabled"] = "true",
            [$"{Providers}:Google:ClientIds:0"] = ClientId,
            [$"{Providers}:Google:ClientIds:1"] = "",
            [$"{Section}:MaxTokenAge"] = "00:00:30",
        });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("Auth:External:Providers:Facebook:AppId");
        failure.Message.ShouldContain("Auth:External:Providers:Okta:Authority");
        failure.Message.ShouldNotContain(Secret);
        failure.Message.ShouldNotContain(ClientId);
        failure.Message.ShouldNotContain("insecure.example.com");
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        AuthModule.AddExternalLogin(services, configuration);
        return services.BuildServiceProvider();
    }
}
