using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using TemplateName.Modules.Auth;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Infrastructure.External;
using TemplateName.Modules.Auth.Infrastructure.Tokens;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Auth.External;

/// <summary>
/// The OIDC path of <see cref="IExternalIdentityVerifier"/> as the module registers it (verifier, replay guard on HybridCache, the
/// <c>oidc-discovery</c> client and its pipeline) against <see cref="FakeIdentityProvider"/>, on fake time. Every refused proof is also
/// checked not to have spent its nonce: a valid token with the same nonce still passes afterwards.
/// </summary>
public sealed class OidcIdTokenVerifierTests
{
    // Years away from the real clock, so a check that read the system clock instead of the TimeProvider would fail.
    private static readonly DateTimeOffset Now = new(2031, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private const string TenantA = "8a2b1c3d-0000-4000-8000-0123456789ab";
    private const string TenantB = "1f0e2d3c-1111-4111-8111-ba9876543210";
    private const string ObjectId = "00000000-0000-0000-66f3-3332eca7ea81";

    [Fact]
    public async Task A_valid_token_yields_the_identity_with_sub_as_subject()
    {
        using var host = new Host();

        var result = await host.VerifyAsync(host.Idp.IdToken());

        var identity = Succeeded(result);
        identity.Provider.ShouldBe("google");
        identity.Subject.ShouldBe(FakeIdentityProvider.DefaultSubject);
        identity.Email.ShouldBe(FakeIdentityProvider.DefaultEmail);
        identity.HasUsableEmail.ShouldBeTrue();
        identity.Name.ShouldBe(FakeIdentityProvider.DefaultName);
    }

    [Fact]
    public async Task Discovery_is_read_at_the_authority_and_cached()
    {
        using var host = new Host();

        Succeeded(await host.VerifyAsync(Token(host, 1, _ => { }), Nonce(1)));
        Succeeded(await host.VerifyAsync(Token(host, 2, _ => { }), Nonce(2)));

        host.Idp.Handler.DiscoveryRequests.ShouldBe(1);
        host.Idp.Handler.KeySetRequests.ShouldBe(1);
        host.Idp.Handler.Requests.First().ShouldBe(new Uri("https://accounts.google.com/.well-known/openid-configuration"));
    }

    [Fact]
    public async Task Googles_bare_issuer_is_accepted()
    {
        using var host = new Host();

        var result = await host.VerifyAsync(host.Idp.IdToken(claims => claims["iss"] = "accounts.google.com"));

        Succeeded(result).Subject.ShouldBe(FakeIdentityProvider.DefaultSubject);
    }

    [Fact]
    public async Task An_ES256_token_from_a_published_key_is_accepted()
    {
        using var host = new Host();

        var result = await host.VerifyAsync(host.Idp.IdToken(signWith: FakeIdentityProvider.Signing.Es256));

        Succeeded(result);
    }

    [Theory]
    [InlineData("wrong-issuer")]
    [InlineData("issuer-with-a-trailing-slash")]
    [InlineData("issuer-in-other-case")]
    [InlineData("no-issuer")]
    [InlineData("wrong-audience")]
    [InlineData("audience-with-a-trailing-slash")]
    [InlineData("audience-in-other-case")]
    [InlineData("audience-array-without-ours")]
    [InlineData("audience-array-with-ours-plus-a-slash")]
    [InlineData("no-audience")]
    [InlineData("expired-beyond-skew")]
    [InlineData("no-expiry")]
    [InlineData("not-before-beyond-skew")]
    [InlineData("issued-longer-ago-than-max-token-age")]
    [InlineData("issued-in-the-future-beyond-skew")]
    [InlineData("no-issued-at")]
    [InlineData("issued-at-as-a-string")]
    [InlineData("issued-at-negative")]
    [InlineData("issued-at-fractional")]
    [InlineData("issued-at-beyond-the-maximum-unix-seconds")]
    [InlineData("issued-at-a-huge-double")]
    [InlineData("no-nonce")]
    [InlineData("different-nonce")]
    [InlineData("empty-nonce")]
    [InlineData("nonce-hash-in-upper-case")]
    [InlineData("nonce-as-a-number")]
    [InlineData("nonce-as-an-array")]
    [InlineData("alg-none")]
    [InlineData("hs256-with-the-public-key")]
    [InlineData("rs384")]
    [InlineData("unknown-kid")]
    [InlineData("unpublished-key-under-a-known-kid")]
    [InlineData("no-kid")]
    [InlineData("tampered-payload")]
    [InlineData("signature-removed")]
    [InlineData("no-sub")]
    [InlineData("empty-sub")]
    [InlineData("blank-sub")]
    [InlineData("sub-as-a-number")]
    [InlineData("sub-over-256-characters")]
    public async Task A_forged_or_unacceptable_token_is_InvalidToken_and_spends_no_nonce(string forgery)
    {
        using var host = new Host();

        var result = await host.VerifyAsync(Forge(host.Idp, forgery));

        Failed(result, ExternalLoginErrors.InvalidToken);
        await host.AssertNonceUnspentAsync();
    }

    [Fact]
    public async Task Skew_is_honoured_for_exp_nbf_and_a_future_iat_and_max_token_age_is_inclusive()
    {
        using var host = new Host();
        var now = Now.ToUnixTimeSeconds();
        var maxAge = (long)TimeSpan.FromMinutes(5).TotalSeconds;

        Succeeded(await host.VerifyAsync(Token(host, 1, claims => Times(claims, iat: now - 60, exp: now - 30)), Nonce(1)));
        Succeeded(await host.VerifyAsync(Token(host, 2, claims => claims["nbf"] = now + 30), Nonce(2)));
        Succeeded(await host.VerifyAsync(Token(host, 3, claims => claims["iat"] = now + 30), Nonce(3)));
        Succeeded(await host.VerifyAsync(Token(host, 4, claims => claims["iat"] = now - maxAge), Nonce(4)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x")]
    [InlineData("a.b")]
    [InlineData("a.b.c")]
    [InlineData("!!!.@@@.###")]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0")]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.%%%%.c2ln")]
    [InlineData("bm90IGpzb24.bm90IGpzb24.c2ln")]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.W10.c2ln")]
    [InlineData("a.b.c.d.e")]
    [InlineData("eyJhbGciOiJSU0EtT0FFUCIsImVuYyI6IkEyNTZHQ00ifQ.a.b.c.d")]
    public async Task Garbage_is_InvalidToken_without_a_request_to_the_provider(string garbage)
    {
        using var host = new Host();

        var result = await host.VerifyAsync(garbage);

        Failed(result, ExternalLoginErrors.InvalidToken);
        host.Idp.Handler.DiscoveryRequests.ShouldBe(0);
        await host.AssertNonceUnspentAsync();
    }

    [Fact]
    public async Task A_100_kB_token_is_InvalidToken_without_a_request_to_the_provider()
    {
        using var host = new Host();
        var huge = new string('a', 50_000) + "." + new string('b', 50_000) + ".c";
        var oneOverTheLimit = host.Idp.IdToken(claims => claims["pad"] = new string('p', ExternalProof.MaxTokenLength));

        Failed(await host.VerifyAsync(huge), ExternalLoginErrors.InvalidToken);
        Failed(await host.VerifyAsync(new string('a', 100_000)), ExternalLoginErrors.InvalidToken);
        Failed(await host.VerifyAsync(oneOverTheLimit), ExternalLoginErrors.InvalidToken);
        host.Idp.Handler.DiscoveryRequests.ShouldBe(0);
    }

    [Fact]
    public async Task A_payload_with_a_duplicate_claim_is_InvalidToken()
    {
        using var host = new Host();
        var parts = host.Idp.IdToken().Split('.');
        var payload = Base64UrlEncoder.Decode(parts[1]);
        var duplicated = payload.Insert(payload.Length - 1, ",\"sub\":\"someone-else\"");

        var result = await host.VerifyAsync($"{parts[0]}.{Base64UrlEncoder.Encode(duplicated)}.{parts[2]}");

        Failed(result, ExternalLoginErrors.InvalidToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short-nonce")]
    public async Task A_posted_nonce_that_is_missing_or_too_short_is_InvalidToken(string? nonce)
    {
        using var host = new Host();
        var token = host.Idp.IdToken(claims => claims["nonce"] = nonce ?? FakeIdentityProvider.DefaultNonce);

        var result = await host.VerifyAsync(token, nonce);

        Failed(result, ExternalLoginErrors.InvalidToken);
        if (!string.IsNullOrEmpty(nonce))
        {
            await host.AssertNotSpentAsync(nonce);
        }

        await host.AssertNonceUnspentAsync();
    }

    [Fact]
    public async Task A_posted_nonce_over_256_characters_is_InvalidToken()
    {
        using var host = new Host();
        var nonce = new string('n', ExternalProof.MaxNonceLength + 1);

        var result = await host.VerifyAsync(host.Idp.IdToken(claims => claims["nonce"] = nonce), nonce);

        Failed(result, ExternalLoginErrors.InvalidToken);
        await host.AssertNotSpentAsync(nonce);
        await host.AssertNonceUnspentAsync();
    }

    [Fact]
    public async Task An_audience_array_holding_ours_and_another_is_accepted()
    {
        using var host = new Host();
        var token = host.Idp.IdToken(claims => claims["aud"] = new[] { "another-client.apps.example", FakeIdentityProvider.DefaultClientId });

        Succeeded(await host.VerifyAsync(token));
    }

    [Fact]
    public async Task The_token_nonce_may_be_the_lower_case_hex_SHA256_of_the_posted_nonce()
    {
        using var host = new Host();
        var token = host.Idp.IdToken(claims => claims["nonce"] = FakeIdentityProvider.HashNonce(FakeIdentityProvider.DefaultNonce));

        var result = await host.VerifyAsync(token);

        Succeeded(result);
    }

    [Fact]
    public async Task The_same_token_is_refused_the_second_time()
    {
        using var host = new Host();
        var token = host.Idp.IdToken();

        Succeeded(await host.VerifyAsync(token));
        Failed(await host.VerifyAsync(token), ExternalLoginErrors.InvalidToken);
    }

    [Fact]
    public async Task A_hashed_nonce_token_cannot_be_replayed_by_posting_the_hash_itself()
    {
        // The token binds H = sha256(raw). Replaying it with H as the "raw" nonce also matches (H equals the token's nonce), so the guard
        // must remember the token's nonce, not the posted one.
        using var host = new Host();
        var hash = FakeIdentityProvider.HashNonce(FakeIdentityProvider.DefaultNonce);
        var token = host.Idp.IdToken(claims => claims["nonce"] = hash);

        Succeeded(await host.VerifyAsync(token, FakeIdentityProvider.DefaultNonce));
        Failed(await host.VerifyAsync(token, hash), ExternalLoginErrors.InvalidToken);
    }

    [Fact]
    public async Task A_replayed_token_stays_refused_until_it_is_too_old_anyway()
    {
        using var host = new Host();
        var token = host.Idp.IdToken(claims => claims["exp"] = Now.AddHours(2).ToUnixTimeSeconds());
        Succeeded(await host.VerifyAsync(token));

        // Within MaxTokenAge the nonce is remembered; beyond it the token is too old anyway.
        host.Time.Advance(TimeSpan.FromMinutes(4));
        Failed(await host.VerifyAsync(token), ExternalLoginErrors.InvalidToken);
        host.Time.Advance(TimeSpan.FromMinutes(2));
        Failed(await host.VerifyAsync(token), ExternalLoginErrors.InvalidToken);
    }

    [Theory]
    [InlineData("apple", true, true)]
    [InlineData("apple", "true", true)]
    [InlineData("apple", "TRUE", false)]
    [InlineData("apple", "True", false)]
    [InlineData("apple", "false", false)]
    [InlineData("apple", false, false)]
    [InlineData("apple", null, false)]
    [InlineData("google", true, true)]
    [InlineData("google", "true", true)]
    [InlineData("google", "yes", false)]
    [InlineData("google", 1, false)]
    public async Task Email_verified_as_a_boolean_or_the_string_true_makes_the_email_usable(string provider, object? emailVerified, bool usable)
    {
        var issuer = provider == "apple" ? "https://appleid.apple.com" : FakeIdentityProvider.GoogleIssuer;
        using var host = new Host(issuer);
        var token = host.Idp.IdToken(claims =>
        {
            claims.Remove("email_verified");
            if (emailVerified is not null)
            {
                claims["email_verified"] = emailVerified;
            }
        });

        var identity = Succeeded(await host.VerifyAsync(token, provider: host.Idp.ToProvider(provider)));

        identity.Email.ShouldBe(FakeIdentityProvider.DefaultEmail);
        identity.HasUsableEmail.ShouldBe(usable);
    }

    [Fact]
    public async Task Trust_email_off_never_gives_a_usable_email()
    {
        using var host = new Host();

        var identity = Succeeded(await host.VerifyAsync(host.Idp.IdToken(), provider: host.Idp.ToProvider("google", trustEmail: false)));

        identity.Email.ShouldBe(FakeIdentityProvider.DefaultEmail);
        identity.HasUsableEmail.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_email_is_never_usable(string email)
    {
        using var host = new Host();

        var identity = Succeeded(await host.VerifyAsync(host.Idp.IdToken(claims => claims["email"] = email)));

        identity.Email.ShouldBeNull();
        identity.HasUsableEmail.ShouldBeFalse();
    }

    [Fact]
    public async Task An_email_over_256_characters_is_dropped_and_one_of_256_is_kept()
    {
        using var host = new Host();
        var tooLong = new string('a', UserExternalLogin.EmailMaxLength - "@example.com".Length + 1) + "@example.com";
        var longest = tooLong[1..];

        var dropped = Succeeded(await host.VerifyAsync(Token(host, 1, claims => claims["email"] = tooLong), Nonce(1)));
        var kept = Succeeded(await host.VerifyAsync(Token(host, 2, claims => claims["email"] = longest), Nonce(2)));

        dropped.Email.ShouldBeNull();
        dropped.HasUsableEmail.ShouldBeFalse();
        kept.Email.ShouldBe(longest);
        kept.HasUsableEmail.ShouldBeTrue();
    }

    [Fact]
    public async Task No_email_claim_gives_no_email()
    {
        using var host = new Host();

        var identity = Succeeded(await host.VerifyAsync(host.Idp.IdToken(claims => claims.Remove("email"))));

        identity.Email.ShouldBeNull();
        identity.HasUsableEmail.ShouldBeFalse();
    }

    [Fact]
    public async Task A_subject_of_256_characters_is_accepted()
    {
        using var host = new Host();
        var subject = new string('s', UserExternalLogin.SubjectMaxLength);

        var identity = Succeeded(await host.VerifyAsync(host.Idp.IdToken(claims => claims["sub"] = subject)));

        identity.Subject.ShouldBe(subject);
    }

    [Fact]
    public async Task A_generic_provider_accepts_its_authority_as_issuer_and_does_not_trust_email_by_default()
    {
        using var host = new Host("https://idp.example.test/oauth2/default");
        var token = host.Idp.IdToken();

        var untrusted = Succeeded(await host.VerifyAsync(token, provider: host.Idp.ToProvider("okta")));
        var trusted = Succeeded(await host.VerifyAsync(
            Token(host, 2, _ => { }),
            Nonce(2),
            host.Idp.ToProvider("okta", trustEmail: true)));

        untrusted.Provider.ShouldBe("okta");
        untrusted.HasUsableEmail.ShouldBeFalse();
        trusted.HasUsableEmail.ShouldBeTrue();
        host.Idp.Handler.Requests.First().ShouldBe(new Uri("https://idp.example.test/oauth2/default/.well-known/openid-configuration"));
    }

    [Fact]
    public async Task A_facebook_limited_login_id_token_goes_through_the_oidc_path_and_never_has_a_usable_email()
    {
        using var host = new Host("https://www.facebook.com");

        var identity = Succeeded(await host.VerifyAsync(host.Idp.IdToken(), provider: host.Idp.ToProvider("facebook")));

        identity.Provider.ShouldBe("facebook");
        identity.Subject.ShouldBe(FakeIdentityProvider.DefaultSubject);
        identity.HasUsableEmail.ShouldBeFalse();
    }

    [Fact]
    public async Task Microsoft_subject_is_tid_and_oid_and_only_xms_edov_makes_the_email_usable()
    {
        using var host = new Host(FakeIdentityProvider.MicrosoftIssuer(TenantA));
        var microsoft = host.Idp.ToProvider("microsoft");

        var verified = Succeeded(await host.VerifyAsync(MicrosoftToken(host, TenantA, 1), Nonce(1), microsoft));
        var emailVerifiedOnly = Succeeded(await host.VerifyAsync(
            MicrosoftToken(host, TenantA, 2, claims => { claims.Remove("xms_edov"); claims["email_verified"] = true; }),
            Nonce(2),
            microsoft));
        var edovFalse = Succeeded(await host.VerifyAsync(
            MicrosoftToken(host, TenantA, 3, claims => claims["xms_edov"] = false),
            Nonce(3),
            microsoft));

        verified.Subject.ShouldBe($"{TenantA}:{ObjectId}");
        verified.HasUsableEmail.ShouldBeTrue();
        emailVerifiedOnly.Email.ShouldBe(FakeIdentityProvider.DefaultEmail);
        emailVerifiedOnly.HasUsableEmail.ShouldBeFalse();
        edovFalse.HasUsableEmail.ShouldBeFalse();
        host.Idp.Handler.Requests.First().ShouldBe(new Uri("https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration"));
    }

    [Fact]
    public async Task Microsoft_subject_is_lower_case()
    {
        var upperTenant = TenantA.ToUpperInvariant();
        using var host = new Host(FakeIdentityProvider.MicrosoftIssuer(upperTenant));

        var identity = Succeeded(await host.VerifyAsync(
            MicrosoftToken(host, upperTenant, 1, claims => claims["oid"] = ObjectId.ToUpperInvariant()),
            Nonce(1),
            host.Idp.ToProvider("microsoft")));

        identity.Subject.ShouldBe($"{TenantA}:{ObjectId}");
    }

    [Theory]
    [InlineData("no-tid")]
    [InlineData("no-oid")]
    [InlineData("blank-oid")]
    [InlineData("tid-not-a-guid")]
    [InlineData("oid-not-a-guid")]
    [InlineData("issuer-of-another-tenant")]
    [InlineData("issuer-with-the-placeholder")]
    [InlineData("tid-as-the-placeholder")]
    public async Task A_microsoft_token_without_a_matching_tid_and_oid_is_InvalidToken(string forgery)
    {
        using var host = new Host(FakeIdentityProvider.MicrosoftIssuer(TenantA));
        var token = MicrosoftToken(host, TenantA, 0, claims =>
        {
            switch (forgery)
            {
                case "no-tid": claims.Remove("tid"); break;
                case "no-oid": claims.Remove("oid"); break;
                case "blank-oid": claims["oid"] = " "; break;
                case "tid-not-a-guid": claims["tid"] = "contoso.example"; claims["iss"] = FakeIdentityProvider.MicrosoftIssuer("contoso.example"); break;
                case "oid-not-a-guid": claims["oid"] = "not-a-guid"; break;
                case "issuer-of-another-tenant": claims["iss"] = FakeIdentityProvider.MicrosoftIssuer(TenantB); break;
                case "issuer-with-the-placeholder": claims["iss"] = FakeIdentityProvider.MicrosoftIssuer("{tenantid}"); break;
                case "tid-as-the-placeholder": claims["tid"] = "{tenantid}"; claims["iss"] = FakeIdentityProvider.MicrosoftIssuer("{tenantid}"); break;
                default: throw new ArgumentOutOfRangeException(nameof(forgery));
            }
        });

        var result = await host.VerifyAsync(token, provider: host.Idp.ToProvider("microsoft"));

        Failed(result, ExternalLoginErrors.InvalidToken);
        await host.AssertNonceUnspentAsync(MicrosoftToken(host, TenantA, 0), host.Idp.ToProvider("microsoft"));
    }

    [Theory]
    [InlineData(TenantA, TenantA, true)]
    [InlineData(TenantA, TenantB, false)]
    [InlineData(TenantA, FakeIdentityProvider.MicrosoftConsumersTenant, false)]
    [InlineData("consumers", FakeIdentityProvider.MicrosoftConsumersTenant, true)]
    [InlineData("consumers", TenantA, false)]
    [InlineData("organizations", TenantA, true)]
    [InlineData("organizations", FakeIdentityProvider.MicrosoftConsumersTenant, false)]
    [InlineData("common", TenantB, true)]
    [InlineData("common", FakeIdentityProvider.MicrosoftConsumersTenant, true)]
    public async Task Microsoft_tenant_setting_decides_which_tid_is_accepted(string tenantSetting, string tokenTenant, bool accepted)
    {
        using var host = new Host(FakeIdentityProvider.MicrosoftIssuer(tokenTenant));
        var microsoft = host.Idp.ToProvider("microsoft", tenantId: tenantSetting);

        var result = await host.VerifyAsync(MicrosoftToken(host, tokenTenant, 0), provider: microsoft);

        if (accepted)
        {
            Succeeded(result).Subject.ShouldBe($"{tokenTenant}:{ObjectId}");
        }
        else
        {
            Failed(result, ExternalLoginErrors.InvalidToken);
        }
    }

    [Fact]
    public async Task Microsoft_with_a_tenant_guid_reads_that_tenants_discovery_document()
    {
        using var host = new Host(FakeIdentityProvider.MicrosoftIssuer(TenantA));

        Succeeded(await host.VerifyAsync(MicrosoftToken(host, TenantA, 0), provider: host.Idp.ToProvider("microsoft", tenantId: TenantA)));

        host.Idp.Handler.Requests.First().ShouldBe(new Uri($"https://login.microsoftonline.com/{TenantA}/v2.0/.well-known/openid-configuration"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Discovery_answering_an_error_is_ProviderUnavailable_and_spends_no_nonce(HttpStatusCode status)
    {
        using var host = new Host();
        var token = host.Idp.IdToken(claims => claims["exp"] = Now.AddHours(2).ToUnixTimeSeconds());
        host.Idp.Handler.FailWith(status);

        var result = await host.RunAsync(host.VerifyAsync(token));

        Failed(result, ExternalLoginErrors.ProviderUnavailable);
        host.Idp.Handler.Recover();
        Succeeded(await host.VerifyAsync(token));
    }

    [Fact]
    public async Task Discovery_timing_out_is_ProviderUnavailable()
    {
        using var host = new Host();
        host.Idp.Handler.Hang();

        var result = await host.RunAsync(host.VerifyAsync(host.Idp.IdToken()));

        Failed(result, ExternalLoginErrors.ProviderUnavailable);
    }

    [Fact]
    public async Task A_key_set_answering_an_error_is_ProviderUnavailable()
    {
        using var host = new Host();
        host.Idp.Handler.FailKeySetWith(HttpStatusCode.InternalServerError);

        var result = await host.RunAsync(host.VerifyAsync(host.Idp.IdToken()));

        Failed(result, ExternalLoginErrors.ProviderUnavailable);
    }

    [Fact]
    public async Task A_token_under_a_freshly_rotated_key_is_accepted_in_the_same_request_after_one_refresh()
    {
        using var host = new Host();
        Succeeded(await host.VerifyAsync(Token(host, 1, _ => { }), Nonce(1)));
        host.Idp.Handler.KeySetRequests.ShouldBe(1);

        host.Idp.RotateKey();
        var identity = Succeeded(await host.VerifyAsync(Token(host, 2, _ => { }), Nonce(2)));

        identity.Subject.ShouldBe(FakeIdentityProvider.DefaultSubject);
        host.Idp.Handler.KeySetRequests.ShouldBe(2);
        Succeeded(await host.VerifyAsync(Token(host, 3, _ => { }), Nonce(3)));
        host.Idp.Handler.KeySetRequests.ShouldBe(2);
    }

    [Fact]
    public async Task A_kid_still_unknown_after_the_refresh_is_InvalidToken_and_refreshes_at_most_once_per_interval()
    {
        using var host = new Host();
        Succeeded(await host.VerifyAsync(Token(host, 1, _ => { }), Nonce(1)));

        // One refresh, one retry, still unknown: refused.
        Failed(await host.VerifyAsync(Token(host, 2, _ => { }, FakeIdentityProvider.Signing.UnknownKey), Nonce(2)), ExternalLoginErrors.InvalidToken);
        host.Idp.Handler.KeySetRequests.ShouldBe(2);

        // Within the refresh interval random kids cannot make us read the provider again.
        host.Time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Failed(await host.VerifyAsync(Token(host, 3, _ => { }, FakeIdentityProvider.Signing.UnknownKey), Nonce(3)), ExternalLoginErrors.InvalidToken);
        host.Idp.Handler.KeySetRequests.ShouldBe(2);

        // Once it has passed, the next unknown kid refreshes once more.
        host.Time.Advance(TimeSpan.FromSeconds(1));
        Failed(await host.VerifyAsync(Token(host, 4, _ => { }, FakeIdentityProvider.Signing.UnknownKey), Nonce(4)), ExternalLoginErrors.InvalidToken);
        host.Idp.Handler.KeySetRequests.ShouldBe(3);
        await host.AssertNonceUnspentAsync();
    }

    [Fact]
    public async Task Concurrent_tokens_under_a_rotated_key_share_one_refresh()
    {
        using var host = new Host();
        Succeeded(await host.VerifyAsync(Token(host, 1, _ => { }), Nonce(1)));
        host.Idp.RotateKey();

        var results = await Task.WhenAll(Enumerable.Range(2, 8).Select(index => host.VerifyAsync(Token(host, index, _ => { }), Nonce(index))));

        results.ShouldAllBe(result => result.IsSuccess);
        host.Idp.Handler.KeySetRequests.ShouldBe(2);
    }

    [Fact]
    public async Task A_refresh_that_cannot_be_fetched_is_ProviderUnavailable()
    {
        using var host = new Host();
        Succeeded(await host.VerifyAsync(Token(host, 1, _ => { }), Nonce(1)));
        host.Idp.RotateKey();
        host.Idp.Handler.FailWith(HttpStatusCode.InternalServerError);

        var result = await host.RunAsync(host.VerifyAsync(Token(host, 2, _ => { }), Nonce(2)));

        Failed(result, ExternalLoginErrors.ProviderUnavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Discovery_without_usable_signing_keys_is_ProviderUnavailable(bool emptyKeySet)
    {
        using var host = new Host();
        if (emptyKeySet)
        {
            host.Idp.Handler.ServeEmptyKeySet();
        }
        else
        {
            host.Idp.Handler.OmitKeySetAddress();
        }

        var result = await host.VerifyAsync(host.Idp.IdToken());

        Failed(result, ExternalLoginErrors.ProviderUnavailable);
        host.Idp.Handler.Recover();
        Failed(await host.VerifyAsync(host.Idp.IdToken()), ExternalLoginErrors.ProviderUnavailable);

        // Once the refresh interval has passed, the published keys are read again and the same nonce still passes.
        host.Time.Advance(TimeSpan.FromMinutes(5));
        await host.AssertNonceUnspentAsync();
    }

    [Fact]
    public async Task Both_proofs_or_neither_is_InvalidToken()
    {
        using var host = new Host();
        var provider = host.Idp.ToProvider();

        Failed(await host.Verifier.VerifyAsync(provider, new ExternalProof(host.Idp.IdToken(), "access-token", FakeIdentityProvider.DefaultNonce), TestContext.Current.CancellationToken), ExternalLoginErrors.InvalidToken);
        Failed(await host.Verifier.VerifyAsync(provider, new ExternalProof(null, null, FakeIdentityProvider.DefaultNonce), TestContext.Current.CancellationToken), ExternalLoginErrors.InvalidToken);
        await host.AssertNonceUnspentAsync();
    }

    [Fact]
    public async Task An_access_token_is_not_accepted_by_an_oidc_provider()
    {
        using var host = new Host();

        var result = await host.Verifier.VerifyAsync(host.Idp.ToProvider(), new ExternalProof(null, "access-token", null), TestContext.Current.CancellationToken);

        Failed(result, ExternalLoginErrors.InvalidToken);
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_reported_as_a_result()
    {
        using var host = new Host();
        host.Idp.Handler.Hang();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => host.Verifier.VerifyAsync(
            host.Idp.ToProvider(),
            new ExternalProof(host.Idp.IdToken(), null, FakeIdentityProvider.DefaultNonce),
            cancellation.Token));
    }

    [Fact]
    public void Proofs_and_identities_print_no_token_nonce_subject_or_email()
    {
        const string Token = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJ4In0.c2ln";
        var proof = new ExternalProof(Token, "access-token-value", FakeIdentityProvider.DefaultNonce);
        var identity = new VerifiedExternalIdentity("google", "subject-value", "alice@example.com", true, "Alice");
        var verified = new VerifiedIdToken(identity, FakeIdentityProvider.DefaultNonce);

        proof.ToString().ShouldBe("ExternalProof");
        identity.ToString().ShouldNotContain("subject-value");
        identity.ToString().ShouldNotContain("alice@example.com");
        identity.ToString().ShouldContain("google");
        verified.ToString().ShouldBe("VerifiedIdToken");
    }

    private static VerifiedExternalIdentity Succeeded(Result<VerifiedExternalIdentity> result)
    {
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.Code : null);
        return result.Value;
    }

    private static void Failed(Result<VerifiedExternalIdentity> result, Error expected)
    {
        result.IsFailure.ShouldBeTrue("the proof was accepted");
        result.Error.ShouldBe(expected);
    }

    /// <summary>
    /// A distinct nonce per token in one test, so a test can verify several tokens without the replay guard in the way; index 0 is
    /// <see cref="FakeIdentityProvider.DefaultNonce"/>, the one <see cref="Host.VerifyAsync"/> posts by default.
    /// </summary>
    private static string Nonce(int index) => index == 0 ? FakeIdentityProvider.DefaultNonce : $"nonce-{index:D4}-0123456789abcdef";

    private static string Token(Host host, int index, Action<IDictionary<string, object>> customize, string? signWith = null) =>
        host.Idp.IdToken(
            claims =>
            {
                claims["nonce"] = Nonce(index);
                customize(claims);
            },
            signWith);

    private static string MicrosoftToken(Host host, string tenant, int index, Action<IDictionary<string, object>>? customize = null) =>
        Token(host, index, claims =>
        {
            claims["iss"] = FakeIdentityProvider.MicrosoftIssuer(tenant);
            claims["tid"] = tenant;
            claims["oid"] = ObjectId;
            claims["sub"] = "pairwise-subject-per-application";
            claims["xms_edov"] = true;
            claims.Remove("email_verified");
            customize?.Invoke(claims);
        });

    private static void Times(IDictionary<string, object> claims, long iat, long exp)
    {
        claims["iat"] = iat;
        claims["nbf"] = iat;
        claims["exp"] = exp;
    }

    private static string Forge(FakeIdentityProvider idp, string forgery)
    {
        var now = Now.ToUnixTimeSeconds();
        var maxAge = (long)TimeSpan.FromMinutes(5).TotalSeconds;
        return forgery switch
        {
            "wrong-issuer" => idp.IdToken(claims => claims["iss"] = "https://accounts.google.com.attacker.example"),
            "issuer-with-a-trailing-slash" => idp.IdToken(claims => claims["iss"] = "https://accounts.google.com/"),
            "issuer-in-other-case" => idp.IdToken(claims => claims["iss"] = "https://Accounts.Google.com"),
            "no-issuer" => idp.IdToken(claims => claims.Remove("iss")),
            "wrong-audience" => idp.IdToken(claims => claims["aud"] = "another-client.apps.example"),
            "audience-with-a-trailing-slash" => idp.IdToken(claims => claims["aud"] = FakeIdentityProvider.DefaultClientId + "/"),
            "audience-in-other-case" => idp.IdToken(claims => claims["aud"] = FakeIdentityProvider.DefaultClientId.ToUpperInvariant()),
            "audience-array-without-ours" => idp.IdToken(claims => claims["aud"] = new[] { "another-client.apps.example", "third.apps.example" }),
            "audience-array-with-ours-plus-a-slash" => idp.IdToken(claims => claims["aud"] = new[] { "another-client.apps.example", FakeIdentityProvider.DefaultClientId + "/" }),
            "no-audience" => idp.IdToken(claims => claims.Remove("aud")),
            "expired-beyond-skew" => idp.IdToken(claims => Times(claims, iat: now - 120, exp: now - 31)),
            "no-expiry" => idp.IdToken(claims => claims.Remove("exp")),
            "not-before-beyond-skew" => idp.IdToken(claims => claims["nbf"] = now + 31),
            "issued-longer-ago-than-max-token-age" => idp.IdToken(claims => Times(claims, iat: now - maxAge - 1, exp: now + 3600)),
            "issued-in-the-future-beyond-skew" => idp.IdToken(claims => claims["iat"] = now + 31),
            "no-issued-at" => idp.IdToken(claims => claims.Remove("iat")),
            "issued-at-as-a-string" => idp.IdToken(claims => claims["iat"] = now.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            "issued-at-negative" => idp.IdToken(claims => claims["iat"] = -1L),
            "issued-at-fractional" => idp.IdToken(claims => claims["iat"] = now - 0.5),
            "issued-at-beyond-the-maximum-unix-seconds" => idp.IdToken(claims => claims["iat"] = 253_402_300_800L),
            "issued-at-a-huge-double" => idp.IdToken(claims => claims["iat"] = 1e300),
            "no-nonce" => idp.IdToken(claims => claims.Remove("nonce")),
            "different-nonce" => idp.IdToken(claims => claims["nonce"] = "another-nonce-0123456789"),
            "empty-nonce" => idp.IdToken(claims => claims["nonce"] = string.Empty),
            "nonce-hash-in-upper-case" => idp.IdToken(claims => claims["nonce"] = FakeIdentityProvider.HashNonce(FakeIdentityProvider.DefaultNonce).ToUpperInvariant()),
            "nonce-as-a-number" => idp.IdToken(claims => claims["nonce"] = 1234567890123456789L),
            "nonce-as-an-array" => idp.IdToken(claims => claims["nonce"] = new[] { FakeIdentityProvider.DefaultNonce }),
            "alg-none" => idp.IdToken(signWith: FakeIdentityProvider.Signing.None),
            "hs256-with-the-public-key" => idp.IdToken(signWith: FakeIdentityProvider.Signing.Hs256WithPublicKey),
            "rs384" => idp.IdToken(signWith: FakeIdentityProvider.Signing.Rs384),
            "unknown-kid" => idp.IdToken(signWith: FakeIdentityProvider.Signing.UnknownKey),
            "unpublished-key-under-a-known-kid" => idp.IdToken(signWith: FakeIdentityProvider.Signing.StrangerUnderKnownKid),
            "no-kid" => idp.IdToken(signWith: FakeIdentityProvider.Signing.NoKid),
            "tampered-payload" => Tamper(idp.IdToken(claims => claims["email_verified"] = false)),
            "signature-removed" => string.Join('.', idp.IdToken().Split('.')[..2]) + ".",
            "no-sub" => idp.IdToken(claims => claims.Remove("sub")),
            "empty-sub" => idp.IdToken(claims => claims["sub"] = string.Empty),
            "blank-sub" => idp.IdToken(claims => claims["sub"] = "   "),
            "sub-as-a-number" => idp.IdToken(claims => claims["sub"] = 1234567890L),
            "sub-over-256-characters" => idp.IdToken(claims => claims["sub"] = new string('s', UserExternalLogin.SubjectMaxLength + 1)),
            _ => throw new ArgumentOutOfRangeException(nameof(forgery), forgery, null),
        };
    }

    private static string Tamper(string token)
    {
        var parts = token.Split('.');
        var payload = JsonNode.Parse(Base64UrlEncoder.Decode(parts[1]))!.AsObject();
        payload["email_verified"] = true;
        payload["sub"] = "victim-subject";
        return $"{parts[0]}.{Base64UrlEncoder.Encode(payload.ToJsonString())}.{parts[2]}";
    }

    /// <summary>The module's external-login services over the fake provider, on a fake clock starting at <see cref="Now"/>.</summary>
    private sealed class Host : IDisposable
    {
        private readonly ServiceProvider _services;

        public Host(string issuer = FakeIdentityProvider.GoogleIssuer)
        {
            Time = new FakeTimeProvider(Now);
            Idp = new FakeIdentityProvider(Time, issuer);

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:External:MaxTokenAge"] = "00:05:00",
                ["Auth:Jwt:ClockSkew"] = "00:00:30",
            }).Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton<IConfiguration>(configuration);
            services.AddOptions<JwtOptions>().Bind(configuration.GetSection(JwtOptions.SectionName));
            AuthModule.AddPermissions(services, configuration);
            AuthModule.AddExternalLogin(services, configuration);
            services.AddHttpClient(ExternalHttpClients.OidcDiscovery).ConfigurePrimaryHttpMessageHandler(() => Idp.Handler);
            _services = services.BuildServiceProvider();
            Verifier = _services.GetRequiredService<IExternalIdentityVerifier>();
        }

        public FakeTimeProvider Time { get; }

        public FakeIdentityProvider Idp { get; }

        public IExternalIdentityVerifier Verifier { get; }

        public Task<Result<VerifiedExternalIdentity>> VerifyAsync(
            string idToken,
            string? nonce = FakeIdentityProvider.DefaultNonce,
            ExternalProvider? provider = null) =>
            Verifier.VerifyAsync(provider ?? Idp.ToProvider(), new ExternalProof(idToken, null, nonce), TestContext.Current.CancellationToken);

        /// <summary>A valid token with <see cref="FakeIdentityProvider.DefaultNonce"/> still passes: the refused proof spent nothing.</summary>
        public async Task AssertNonceUnspentAsync(string? validToken = null, ExternalProvider? provider = null)
        {
            Idp.Handler.Recover();
            var result = await VerifyAsync(validToken ?? Idp.IdToken(), provider: provider);
            result.IsSuccess.ShouldBeTrue($"the nonce was spent by a refused proof ({(result.IsFailure ? result.Error.Code : "")})");
        }

        /// <summary>Asks the replay guard itself: <paramref name="nonce"/> was never spent for the provider (claiming it now succeeds).</summary>
        public async Task AssertNotSpentAsync(string nonce, string provider = "google")
        {
            var guard = _services.GetRequiredService<IExternalProofReplayGuard>();
            (await guard.TryClaimAsync(provider, nonce, TestContext.Current.CancellationToken)).ShouldBeTrue("the refused proof spent its nonce");
        }

        /// <summary>Moves the fake clock while <paramref name="call"/> runs, so the pipeline's delays and timeouts elapse.</summary>
        public async Task<T> RunAsync<T>(Task<T> call)
        {
            var started = TimeProvider.System.GetTimestamp();
            while (!call.IsCompleted)
            {
                if (TimeProvider.System.GetElapsedTime(started) > TimeSpan.FromSeconds(20))
                {
                    throw new TimeoutException("The call did not complete while the fake time moved.");
                }

                Time.Advance(TimeSpan.FromMilliseconds(500));
                await Task.Delay(5);
            }

            return await call;
        }

        public void Dispose()
        {
            _services.Dispose();
            Idp.Dispose();
        }
    }
}
