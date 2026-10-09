using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Infrastructure.Tokens;

namespace TemplateName.UnitTests.Auth;

public sealed class AccessTokenIssuerTests
{
    // Not on a whole second, so the test sees that iat, nbf and exp are truncated to the NumericDate the token can carry.
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 8, 30, 15, 750, TimeSpan.Zero);
    private static readonly DateTimeOffset SignedInAt = new(2026, 10, 8, 8, 0, 0, TimeSpan.Zero);

    private static readonly AccessTokenRequest Request = new(
        UserId: Guid.Parse("0199b0a3-5f00-7c3e-9a51-2a8f4c6b7d10"),
        SessionId: Guid.Parse("0199b0a3-6000-7d4f-8b62-3b9f5d7c8e21"),
        SecurityStamp: "STAMP-1234567890",
        AuthMethods: "pwd otp",
        AuthTime: SignedInAt,
        Locale: "ms");

    private readonly FakeTimeProvider _time = new(Now);
    private readonly JwtOptions _options = TestSigningKeys.Options(TestSigningKeys.Active("key-active"));
    private readonly SigningKeyProvider _keys;
    private readonly AccessTokenIssuer _issuer;

    public AccessTokenIssuerTests()
    {
        _keys = TestSigningKeys.Provider(_options);
        _issuer = new AccessTokenIssuer(_keys, TestSigningKeys.Wrap(_options), _time);
    }

    [Fact]
    public async Task Issued_token_validates_with_the_public_key()
    {
        var token = _issuer.Issue(Request);

        // Only what /.well-known/jwks.json publishes: a verifier holding no secret must accept the token.
        var publishedKey = _keys.PublicKeySet.Keys.ShouldHaveSingleItem();
        var parameters = JwtValidation.CreateParameters(_options, _keys, _time);
        parameters.IssuerSigningKeyResolver = (_, _, kid, _) => kid == publishedKey.Kid ? [publishedKey] : [];

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Value, parameters);

        result.IsValid.ShouldBeTrue(result.Exception?.ToString());
        result.ClaimsIdentity.FindFirst("sub")!.Value.ShouldBe(Request.UserId.ToString());
    }

    [Fact]
    public void Header_has_alg_ES256_and_kid()
    {
        var token = new JsonWebToken(_issuer.Issue(Request).Value);

        token.Alg.ShouldBe("ES256");
        token.Kid.ShouldBe("key-active");
        token.Typ.ShouldBe("JWT");
    }

    [Fact]
    public void Claims_contain_sub_sid_sst_amr_auth_time_locale_and_no_permissions_or_email()
    {
        var first = _issuer.Issue(Request);
        var second = _issuer.Issue(Request);

        using var payload = Payload(first.Value);
        var root = payload.RootElement;

        root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ShouldBe(
            ["amr", "aud", "auth_time", "exp", "iat", "iss", "jti", "locale", "nbf", "sid", "sst", "sub"]);
        root.GetProperty("sub").GetString().ShouldBe(Request.UserId.ToString());
        root.GetProperty("sid").GetString().ShouldBe(Request.SessionId.ToString());
        root.GetProperty("sst").GetString().ShouldBe(Request.SecurityStamp);
        root.GetProperty("amr").ValueKind.ShouldBe(JsonValueKind.Array);
        root.GetProperty("amr").EnumerateArray().Select(method => method.GetString()).ShouldBe(["pwd", "otp"]);
        root.GetProperty("auth_time").ValueKind.ShouldBe(JsonValueKind.Number);
        root.GetProperty("auth_time").GetInt64().ShouldBe(SignedInAt.ToUnixTimeSeconds());
        root.GetProperty("locale").GetString().ShouldBe("ms");
        root.GetProperty("iss").GetString().ShouldBe(TestSigningKeys.Issuer);
        root.GetProperty("aud").GetString().ShouldBe(TestSigningKeys.Audience);

        var jti = root.GetProperty("jti").GetString();
        jti.ShouldNotBeNullOrWhiteSpace();
        new JsonWebToken(second.Value).Id.ShouldNotBe(jti);
    }

    [Fact]
    public void A_single_auth_method_is_still_an_array()
    {
        var token = _issuer.Issue(Request with { AuthMethods = "pwd" });

        using var payload = Payload(token.Value);

        var amr = payload.RootElement.GetProperty("amr");
        amr.ValueKind.ShouldBe(JsonValueKind.Array);
        amr.EnumerateArray().Select(method => method.GetString()).ShouldBe(["pwd"]);
    }

    [Fact]
    public void Expiry_is_ten_minutes_after_issue_using_TimeProvider()
    {
        var token = _issuer.Issue(Request);

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(Now.ToUnixTimeSeconds());
        using var payload = Payload(token.Value);
        var root = payload.RootElement;
        root.GetProperty("iat").GetInt64().ShouldBe(issuedAt.ToUnixTimeSeconds());
        root.GetProperty("nbf").GetInt64().ShouldBe(issuedAt.ToUnixTimeSeconds());
        root.GetProperty("exp").GetInt64().ShouldBe(issuedAt.AddMinutes(10).ToUnixTimeSeconds());
        token.ExpiresAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64()));
        token.ExpiresAt.ShouldBe(issuedAt.AddMinutes(10));

        _time.Advance(TimeSpan.FromMinutes(3));
        _issuer.Issue(Request).ExpiresAt.ShouldBe(issuedAt.AddMinutes(13));
    }

    [Fact]
    public void Lifetime_comes_from_options()
    {
        _options.AccessTokenLifetime = TimeSpan.FromMinutes(5);

        var token = _issuer.Issue(Request);

        token.ExpiresAt.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(Now.ToUnixTimeSeconds()).AddMinutes(5));
    }

    private static JsonDocument Payload(string token)
    {
        var encodedPayload = token.Split('.')[1];
        return JsonDocument.Parse(Encoding.UTF8.GetString(Base64UrlEncoder.DecodeBytes(encodedPayload)));
    }
}
