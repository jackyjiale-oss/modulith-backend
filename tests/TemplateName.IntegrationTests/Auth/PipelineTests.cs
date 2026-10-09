using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Resources;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Infrastructure.Tokens;
using TemplateName.Web.Common.Resources;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// The host pipeline with authentication and the fallback policy (decision D7): anonymous endpoints stay reachable, everything else
/// needs a valid access token, and forged or odd tokens (Review Focus 4) get 401, never 500. <c>GET /test/protected</c> is a test-only
/// endpoint protected by the fallback policy alone (<see cref="ProtectedTestEndpointStartupFilter"/>).
/// </summary>
public sealed class PipelineTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string ProtectedRoute = ProtectedTestEndpointStartupFilter.Route;

    // Times come from the descriptor only, so forged tokens follow the factory's clock.
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    private ISigningKeyProvider Keys => Factory.Services.GetRequiredService<ISigningKeyProvider>();

    private JwtOptions Jwt => Factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/scalar/v1")]
    [InlineData("/.well-known/jwks.json")]
    public async Task Health_openapi_and_jwks_are_reachable_anonymously(string path)
    {
        using var response = await Client.GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Unknown_protected_route_without_token_returns_401_problem_details_with_trace_id_and_code_http_401()
    {
        foreach (var path in new[] { "/api/v1/does-not-exist", ProtectedRoute })
        {
            using var response = await Client.GetAsync(path, Ct);

            await AssertUnauthorizedProblemAsync(response);
        }
    }

    [Fact]
    public async Task Valid_token_reaches_the_protected_endpoint_as_the_signed_in_caller()
    {
        var userId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var token = TestAccessTokens.Issue(Factory.Services, userId, sessionId).Value;

        using var response = await SendAsync(token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var caller = await response.Content.ReadFromJsonAsync<ProtectedTestEndpointStartupFilter.CallerResponse>(Ct);
        caller.ShouldNotBeNull();
        caller.IsAuthenticated.ShouldBeTrue();
        caller.UserId.ShouldBe(userId);
        caller.SessionId.ShouldBe(sessionId);
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    [InlineData("eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl")]
    public async Task Garbage_bearer_token_returns_401_not_500(string token)
    {
        using var response = await SendAsync(token);

        await AssertUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task Alg_none_token_returns_401()
    {
        // No signing credentials: the library writes an unsigned token with "alg":"none".
        var unsigned = _handler.CreateToken(Descriptor(signingCredentials: null));
        new JsonWebToken(unsigned).Alg.ShouldBe("none");

        using var response = await SendAsync(unsigned);

        await AssertUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task HS256_token_signed_with_the_public_key_bytes_returns_401()
    {
        // Algorithm confusion: the public key is published, so an attacker can use its bytes as an HMAC secret under the real kid.
        var activeKey = (ECDsaSecurityKey)Keys.Active.Key;
        var publicKeyPem = activeKey.ECDsa.ExportSubjectPublicKeyInfoPem();
        foreach (var secret in new[] { Encoding.UTF8.GetBytes(publicKeyPem), activeKey.ECDsa.ExportSubjectPublicKeyInfo() })
        {
            var hmacKey = new SymmetricSecurityKey(secret) { KeyId = activeKey.KeyId };
            var forged = _handler.CreateToken(Descriptor(new SigningCredentials(hmacKey, SecurityAlgorithms.HmacSha256)));
            new JsonWebToken(forged).Alg.ShouldBe("HS256");

            using var response = await SendAsync(forged);

            await AssertUnauthorizedProblemAsync(response);
        }
    }

    [Fact]
    public async Task Unknown_kid_returns_401()
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = _handler.CreateToken(Descriptor(new SigningCredentials(
            new ECDsaSecurityKey(stranger) { KeyId = "key-unknown" },
            SecurityAlgorithms.EcdsaSha256)));

        using var response = await SendAsync(forged);

        await AssertUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task Tampered_payload_returns_401()
    {
        var parts = TestAccessTokens.Issue(Factory.Services).Value.Split('.');
        var payload = JsonNode.Parse(Base64UrlEncoder.Decode(parts[1]))!.AsObject();
        payload["sub"] = Guid.NewGuid().ToString();
        var tampered = $"{parts[0]}.{Base64UrlEncoder.Encode(payload.ToJsonString())}.{parts[2]}";

        using var response = await SendAsync(tampered);

        await AssertUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task Wrong_audience_returns_401()
    {
        // The same hand-built token with the right audience is accepted, so only the audience makes the difference.
        using var control = await SendAsync(_handler.CreateToken(Descriptor(Keys.Active)));
        control.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var response = await SendAsync(_handler.CreateToken(Descriptor(Keys.Active, audience: "another-api")));

        await AssertUnauthorizedProblemAsync(response);
    }

    [Fact]
    public async Task Expired_token_returns_401()
    {
        var token = TestAccessTokens.Issue(Factory.Services);
        var skew = Jwt.ClockSkew;

        Factory.Time.SetUtcNow(token.ExpiresAt + skew);
        using var withinSkew = await SendAsync(token.Value);
        Factory.Time.SetUtcNow(token.ExpiresAt + skew + TimeSpan.FromSeconds(1));
        using var expired = await SendAsync(token.Value);

        withinSkew.StatusCode.ShouldBe(HttpStatusCode.OK);
        await AssertUnauthorizedProblemAsync(expired);
    }

    [Fact]
    public async Task Unauthenticated_401_is_localized_from_accept_language()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedRoute);
        request.Headers.TryAddWithoutValidation("Accept-Language", "ms");

        using var response = await Client.SendAsync(request, Ct);

        var body = await AssertUnauthorizedProblemAsync(response);
        body.GetProperty("detail").GetString().ShouldBe(Resource("http.401", "ms"));
    }

    [Fact]
    public async Task Saved_locale_claim_wins_over_accept_language()
    {
        var token = TestAccessTokens.Issue(Factory.Services, locale: "ms").Value;

        using var response = await SendAsync(token, acceptLanguage: "zh-Hans");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentLanguage.ShouldHaveSingleItem().ShouldBe("ms");
        var caller = await response.Content.ReadFromJsonAsync<ProtectedTestEndpointStartupFilter.CallerResponse>(Ct);
        caller!.UICulture.ShouldBe("ms");
        caller.Culture.ShouldBe("en");
    }

    [Fact]
    public async Task Unsupported_locale_claim_falls_through_to_accept_language()
    {
        var token = TestAccessTokens.Issue(Factory.Services, locale: "fr-FR").Value;

        using var response = await SendAsync(token, acceptLanguage: "zh-Hans");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var caller = await response.Content.ReadFromJsonAsync<ProtectedTestEndpointStartupFilter.CallerResponse>(Ct);
        caller!.UICulture.ShouldBe("zh-Hans");
    }

    private async Task<HttpResponseMessage> SendAsync(string token, string? acceptLanguage = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ProtectedRoute).WithBearer(token);
        if (acceptLanguage is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
        }

        return await Client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> AssertUnauthorizedProblemAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldContain(header => header.Scheme == "Bearer");
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetInt32().ShouldBe(401);
        body.GetProperty("code").GetString().ShouldBe("http.401");
        body.GetProperty("traceId").GetString().ShouldBe(response.Headers.GetValues("X-Trace-Id").Single());
        return body;
    }

    private SecurityTokenDescriptor Descriptor(SigningCredentials? signingCredentials, string? audience = null)
    {
        var now = Factory.Time.GetUtcNow();
        return new SecurityTokenDescriptor
        {
            Issuer = Jwt.Issuer,
            Audience = audience ?? Jwt.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(10).UtcDateTime,
            SigningCredentials = signingCredentials,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Guid.NewGuid().ToString(),
                ["sid"] = Guid.NewGuid().ToString(),
                ["sst"] = TestAccessTokens.SecurityStamp,
                ["locale"] = "en",
                ["jti"] = Guid.NewGuid().ToString(),
            },
        };
    }

    /// <summary>The translation itself (no parent fallback), so a missing entry fails instead of comparing English with English.</summary>
    private static string Resource(string key, string culture)
        => new ResourceManager(typeof(CommonErrorMessages).FullName!, typeof(CommonErrorMessages).Assembly)
            .GetResourceSet(CultureInfo.GetCultureInfo(culture), createIfNotExists: true, tryParents: false)!
            .GetString(key)!;
}
