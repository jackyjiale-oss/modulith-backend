using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.UnitTests.Auth.External;

public sealed class ExternalProviderCatalogTests
{
    private const string Providers = "Auth:External:Providers";

    [Fact]
    public void Google_preset_accepts_both_issuer_spellings_and_trusts_a_verified_gmail_or_workspace_email()
    {
        // Ruling R15: Google's email_verified is authoritative only for a Gmail address or a Workspace (hd) token.
        var google = Enabled(("Google:Enabled", "true"), ("Google:ClientIds:0", "web.apps.example"), ("Google:ClientIds:1", "ios.apps.example"))
            .Find("google").ShouldNotBeNull();

        google.Name.ShouldBe("google");
        google.DisplayName.ShouldBe("Google");
        google.Kind.ShouldBe(ExternalProviderKind.Oidc);
        google.Authority.ShouldBe("https://accounts.google.com");
        google.Issuers.ShouldBe(["https://accounts.google.com", "accounts.google.com"]);
        google.ClientIds.ShouldBe(["web.apps.example", "ios.apps.example"]);
        google.EmailRule.ShouldBe(ExternalEmailRule.GoogleVerifiedGmailOrWorkspace);
        google.TrustEmail.ShouldBeTrue();
        google.TenantId.ShouldBeNull();
        google.AppId.ShouldBeNull();
        google.AppSecret.ShouldBeNull();
        google.MetadataAddress.ShouldBeNull();
    }

    [Fact]
    public void Apple_preset_has_its_issuer_and_trusts_a_verified_email_claim()
    {
        var apple = Enabled(("Apple:Enabled", "true"), ("Apple:ClientIds:0", "com.example.web"))
            .Find("apple").ShouldNotBeNull();

        apple.DisplayName.ShouldBe("Apple");
        apple.Kind.ShouldBe(ExternalProviderKind.Oidc);
        apple.Authority.ShouldBe("https://appleid.apple.com");
        apple.Issuers.ShouldBe(["https://appleid.apple.com"]);
        apple.EmailRule.ShouldBe(ExternalEmailRule.EmailVerifiedClaim);
        apple.TrustEmail.ShouldBeTrue();
    }

    [Fact]
    public void Microsoft_preset_uses_the_tenant_placeholder_issuer_and_the_domain_verified_rule()
    {
        var microsoft = Enabled(("Microsoft:Enabled", "true"), ("Microsoft:ClientIds:0", "ms-client"))
            .Find("microsoft").ShouldNotBeNull();

        microsoft.DisplayName.ShouldBe("Microsoft");
        microsoft.Kind.ShouldBe(ExternalProviderKind.Oidc);
        microsoft.Issuers.ShouldBe(["https://login.microsoftonline.com/{tenantid}/v2.0"]);
        microsoft.EmailRule.ShouldBe(ExternalEmailRule.MicrosoftDomainVerified);
        microsoft.TrustEmail.ShouldBeTrue();
        microsoft.TenantId.ShouldBe("common");
        microsoft.Authority.ShouldBe("https://login.microsoftonline.com/common/v2.0");
    }

    [Fact]
    public void Microsoft_with_a_specific_tenant_discovers_through_that_tenant_and_keeps_it_lower_case()
    {
        var microsoft = Enabled(
                ("Microsoft:Enabled", "true"),
                ("Microsoft:ClientIds:0", "ms-client"),
                ("Microsoft:TenantId", " 8A2B1C3D-0000-4000-8000-0123456789AB "))
            .Find("microsoft").ShouldNotBeNull();

        microsoft.TenantId.ShouldBe("8a2b1c3d-0000-4000-8000-0123456789ab");
        microsoft.Authority.ShouldBe("https://login.microsoftonline.com/8a2b1c3d-0000-4000-8000-0123456789ab/v2.0");
        microsoft.Issuers.ShouldBe(["https://login.microsoftonline.com/{tenantid}/v2.0"]);
    }

    [Fact]
    public void Facebook_preset_has_its_issuer_never_trusts_an_email_and_takes_the_app_id_as_audience()
    {
        var facebook = Enabled(
                ("Facebook:Enabled", "true"),
                ("Facebook:AppId", " 1234567890 "),
                ("Facebook:AppSecret", "the-facebook-secret"),
                ("Facebook:TrustEmail", "true"))
            .Find("facebook").ShouldNotBeNull();

        facebook.DisplayName.ShouldBe("Facebook");
        facebook.Kind.ShouldBe(ExternalProviderKind.Facebook);
        facebook.Authority.ShouldBe("https://www.facebook.com");
        facebook.Issuers.ShouldBe(["https://www.facebook.com"]);
        facebook.ClientIds.ShouldBe(["1234567890"]);
        facebook.EmailRule.ShouldBe(ExternalEmailRule.Never);
        facebook.TrustEmail.ShouldBeFalse();
        facebook.AppId.ShouldBe("1234567890");
        facebook.AppSecret.ShouldBe("the-facebook-secret");

        // Facebook's discovery address ends with a slash and is fetched exactly as written (ruling R7).
        facebook.MetadataAddress.ShouldBe("https://www.facebook.com/.well-known/openid-configuration/");
    }

    [Fact]
    public void A_generic_oidc_entry_uses_its_authority_as_issuer_and_does_not_trust_an_email_by_default()
    {
        var okta = Enabled(
                ("Okta:Enabled", "true"),
                ("Okta:Type", "oidc"),
                ("Okta:Authority", " https://example.okta.com/oauth2/default "),
                ("Okta:ClientIds:0", "okta-client"))
            .Find("okta").ShouldNotBeNull();

        okta.Name.ShouldBe("okta");
        okta.DisplayName.ShouldBe("Okta");
        okta.Kind.ShouldBe(ExternalProviderKind.Oidc);
        okta.Authority.ShouldBe("https://example.okta.com/oauth2/default");
        okta.Issuers.ShouldBe(["https://example.okta.com/oauth2/default"]);
        okta.EmailRule.ShouldBe(ExternalEmailRule.EmailVerifiedClaim);
        okta.TrustEmail.ShouldBeFalse();
    }

    [Fact]
    public void Trust_email_and_display_name_can_be_set_per_entry()
    {
        var catalog = Enabled(
            ("Google:Enabled", "true"),
            ("Google:ClientIds:0", "web.apps.example"),
            ("Google:TrustEmail", "false"),
            ("Google:DisplayName", "Sign in with Google"),
            ("Okta:Enabled", "true"),
            ("Okta:Type", "oidc"),
            ("Okta:Authority", "https://example.okta.com"),
            ("Okta:ClientIds:0", "okta-client"),
            ("Okta:TrustEmail", "true"),
            ("Okta:DisplayName", "Company SSO"));

        var google = catalog.Find("google").ShouldNotBeNull();
        google.TrustEmail.ShouldBeFalse();
        google.DisplayName.ShouldBe("Sign in with Google");
        var okta = catalog.Find("okta").ShouldNotBeNull();
        okta.TrustEmail.ShouldBeTrue();
        okta.DisplayName.ShouldBe("Company SSO");
    }

    [Fact]
    public void Find_is_case_insensitive_and_names_are_trimmed_lower_case()
    {
        var catalog = Enabled((" GoogLe :Enabled", "true"), (" GoogLe :ClientIds:0", "web.apps.example"));

        var upper = catalog.Find("GOOGLE");
        var lower = catalog.Find("google");

        upper.ShouldNotBeNull();
        upper.ShouldBe(lower);
        upper.Name.ShouldBe("google");
        catalog.Find("  Google ").ShouldBe(lower);
        catalog.Enabled.ShouldHaveSingleItem().Name.ShouldBe("google");
    }

    [Fact]
    public void Find_answers_null_for_an_unknown_blank_or_disabled_provider()
    {
        var catalog = Enabled(
            ("Google:Enabled", "true"),
            ("Google:ClientIds:0", "web.apps.example"),
            ("Apple:Enabled", "false"),
            ("Apple:ClientIds:0", "com.example.web"));

        catalog.Find("apple").ShouldBeNull();
        catalog.Find("unknown").ShouldBeNull();
        catalog.Find("").ShouldBeNull();
        catalog.Find("   ").ShouldBeNull();
    }

    [Fact]
    public void Enabled_lists_only_enabled_providers_in_name_order()
    {
        var catalog = Enabled(
            ("Microsoft:Enabled", "true"),
            ("Microsoft:ClientIds:0", "ms-client"),
            ("Apple:Enabled", "false"),
            ("Google:Enabled", "true"),
            ("Google:ClientIds:0", "web.apps.example"),
            ("Facebook:Enabled", "true"),
            ("Facebook:AppId", "1"),
            ("Facebook:AppSecret", "s"));

        catalog.Enabled.Select(provider => provider.Name).ShouldBe(["facebook", "google", "microsoft"]);
    }

    [Fact]
    public void Display_name_of_answers_for_a_disabled_provider_and_falls_back_to_the_name()
    {
        var catalog = Enabled(
            ("Apple:Enabled", "false"),
            ("Okta:Enabled", "false"),
            ("Okta:DisplayName", "Company SSO"),
            ("Acme:Enabled", "false"));

        catalog.DisplayNameOf("apple").ShouldBe("Apple");
        catalog.DisplayNameOf("APPLE").ShouldBe("Apple");
        catalog.DisplayNameOf("okta").ShouldBe("Company SSO");
        catalog.DisplayNameOf("acme").ShouldBe("Acme");
        catalog.DisplayNameOf("never-configured").ShouldBe("never-configured");
    }

    [Fact]
    public void A_providers_to_string_prints_its_name_and_never_the_secret()
    {
        const string Secret = "the-facebook-secret";
        var facebook = Enabled(
                ("Facebook:Enabled", "true"),
                ("Facebook:AppId", "1234567890"),
                ("Facebook:AppSecret", Secret))
            .Find("facebook").ShouldNotBeNull();

        facebook.ToString().ShouldBe("facebook");
        $"{facebook}".ShouldNotContain(Secret);
        new ExternalProvider("x", "X", ExternalProviderKind.Facebook, null, [], [], ExternalEmailRule.Never, false, null, "1", Secret)
            .ToString().ShouldBe("x");
    }

    private static IExternalProviderCatalog Enabled(params (string Key, string Value)[] settings)
    {
        var values = settings.ToDictionary(setting => $"{Providers}:{setting.Key}", setting => (string?)setting.Value);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        AuthModule.AddExternalLogin(services, configuration);
        return services.BuildServiceProvider().GetRequiredService<IExternalProviderCatalog>();
    }
}
