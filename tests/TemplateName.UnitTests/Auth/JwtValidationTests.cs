using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Infrastructure.Tokens;

namespace TemplateName.UnitTests.Auth;

/// <summary>Review Focus 4: forged or odd tokens are rejected by the parameters the bearer handler uses. Forgeries are built with the same library.</summary>
public sealed class JwtValidationTests
{
    // Years away from the real clock, so a check that read the system clock instead of the TimeProvider would fail.
    private static readonly DateTimeOffset Now = new(2031, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly AccessTokenRequest Request = new(
        Guid.Parse("0199b0a3-5f00-7c3e-9a51-2a8f4c6b7d10"),
        Guid.Parse("0199b0a3-6000-7d4f-8b62-3b9f5d7c8e21"),
        "STAMP-1234567890",
        "pwd",
        Now,
        "en");

    private readonly FakeTimeProvider _time = new(Now);
    // Times come from the descriptor only, so a token without "exp" can be built.
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };
    private readonly string _activePrivateKeyPem = TestSigningKeys.PrivateKeyPem();
    private readonly string _retiredPrivateKeyPem = TestSigningKeys.PrivateKeyPem();
    private readonly JwtOptions _options;
    private readonly SigningKeyProvider _keys;

    public JwtValidationTests()
    {
        _options = TestSigningKeys.Options(
            new JwtOptions.SigningKey { KeyId = "key-active", PrivateKeyPem = _activePrivateKeyPem },
            TestSigningKeys.Retired("key-retired", _retiredPrivateKeyPem));
        _keys = TestSigningKeys.Provider(_options);
    }

    [Fact]
    public void Parameters_allow_only_ES256_and_check_issuer_audience_and_lifetime()
    {
        var parameters = JwtValidation.CreateParameters(_options, _keys);

        parameters.ValidAlgorithms.ShouldBe(["ES256"]);
        parameters.ValidateIssuer.ShouldBeTrue();
        parameters.ValidIssuer.ShouldBe(TestSigningKeys.Issuer);
        parameters.ValidateAudience.ShouldBeTrue();
        parameters.ValidAudience.ShouldBe(TestSigningKeys.Audience);
        parameters.ValidateLifetime.ShouldBeTrue();
        parameters.RequireExpirationTime.ShouldBeTrue();
        parameters.RequireSignedTokens.ShouldBeTrue();
        parameters.ClockSkew.ShouldBe(TimeSpan.FromSeconds(30));
        parameters.TryAllIssuerSigningKeys.ShouldBeFalse();
        parameters.IssuerSigningKey.ShouldBeNull();
        parameters.IssuerSigningKeys.ShouldBeNull();
        parameters.IssuerSigningKeyResolver.ShouldNotBeNull();
        parameters.LifetimeValidator.ShouldNotBeNull();
    }

    [Fact]
    public async Task Issued_token_is_accepted()
    {
        var result = await ValidateAsync(Issue());

        result.IsValid.ShouldBeTrue(result.Exception?.ToString());
        result.ClaimsIdentity.FindFirst("sid")!.Value.ShouldBe(Request.SessionId.ToString());
    }

    [Fact]
    public async Task alg_none_token_is_rejected()
    {
        // No signing credentials: the library writes an unsigned token with "alg":"none".
        var unsigned = _handler.CreateToken(Descriptor(signingCredentials: null));
        new JsonWebToken(unsigned).Alg.ShouldBe("none");

        var result = await ValidateAsync(unsigned);

        result.IsValid.ShouldBeFalse();
        result.Exception.ShouldBeOfType<SecurityTokenInvalidSignatureException>();
    }

    [Fact]
    public async Task HS256_token_signed_with_the_public_key_bytes_is_rejected()
    {
        // Algorithm confusion: the attacker knows the public key (it is published) and uses its bytes as an HMAC secret, under the real kid.
        var publicKeyPem = TestSigningKeys.PublicKeyPemOf(_activePrivateKeyPem);
        foreach (var secret in new[] { Encoding.UTF8.GetBytes(publicKeyPem), PublicKeyDer(_activePrivateKeyPem) })
        {
            var hmacKey = new SymmetricSecurityKey(secret) { KeyId = "key-active" };
            var forged = _handler.CreateToken(Descriptor(new SigningCredentials(hmacKey, SecurityAlgorithms.HmacSha256)));
            new JsonWebToken(forged).Alg.ShouldBe("HS256");

            var result = await ValidateAsync(forged);

            result.IsValid.ShouldBeFalse();
            result.Exception.ShouldBeOfType<SecurityTokenInvalidSignatureException>();
        }
    }

    [Fact]
    public async Task Unknown_kid_is_rejected()
    {
        // Signed by a key the server never configured, under a kid it does not know.
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = Issue(new SigningCredentials(new ECDsaSecurityKey(stranger) { KeyId = "key-unknown" }, SecurityAlgorithms.EcdsaSha256));

        var result = await ValidateAsync(forged);

        result.IsValid.ShouldBeFalse();
        result.Exception.ShouldBeOfType<SecurityTokenSignatureKeyNotFoundException>();
    }

    [Fact]
    public async Task Token_without_kid_is_rejected_even_when_signed_by_a_configured_key()
    {
        // A missing kid tries no key at all: there is no fall back to trying every configured key.
        using var activeKey = ECDsa.Create();
        activeKey.ImportFromPem(_activePrivateKeyPem);
        var noKid = Issue(new SigningCredentials(new ECDsaSecurityKey(activeKey), SecurityAlgorithms.EcdsaSha256));
        new JsonWebToken(noKid).Kid.ShouldBeNullOrEmpty();

        var result = await ValidateAsync(noKid);

        result.IsValid.ShouldBeFalse();
        result.Exception.ShouldBeOfType<SecurityTokenSignatureKeyNotFoundException>();
    }

    [Fact]
    public async Task Token_whose_kid_names_another_configured_key_is_rejected()
    {
        // Signed by the retired key but labelled as the active one: only the key the kid names is tried.
        using var retiredKey = ECDsa.Create();
        retiredKey.ImportFromPem(_retiredPrivateKeyPem);
        var mislabelled = Issue(new SigningCredentials(new ECDsaSecurityKey(retiredKey) { KeyId = "key-active" }, SecurityAlgorithms.EcdsaSha256));

        var result = await ValidateAsync(mislabelled);

        result.IsValid.ShouldBeFalse();
        result.Exception.ShouldBeOfType<SecurityTokenInvalidSignatureException>();
    }

    [Fact]
    public async Task Tampered_payload_is_rejected()
    {
        var parts = Issue().Split('.');
        var payload = JsonNode.Parse(Base64UrlEncoder.Decode(parts[1]))!.AsObject();
        payload["sub"] = Guid.Parse("0199b0a3-0000-7000-8000-000000000001").ToString();
        var tampered = $"{parts[0]}.{Base64UrlEncoder.Encode(payload.ToJsonString())}.{parts[2]}";

        var result = await ValidateAsync(tampered);

        result.IsValid.ShouldBeFalse();
        result.Exception.ShouldBeOfType<SecurityTokenInvalidSignatureException>();
    }

    [Fact]
    public async Task Wrong_audience_is_rejected()
    {
        var token = _handler.CreateToken(Descriptor(ActiveCredentials(), audience: "another-api"));

        var result = await ValidateAsync(token);

        result.IsValid.ShouldBeFalse();
        result.Exception.ShouldBeOfType<SecurityTokenInvalidAudienceException>();
    }

    [Fact]
    public async Task Wrong_issuer_is_rejected()
    {
        var token = _handler.CreateToken(Descriptor(ActiveCredentials(), issuer: "https://attacker.test"));

        var result = await ValidateAsync(token);

        result.IsValid.ShouldBeFalse();
        result.Exception.ShouldBeOfType<SecurityTokenInvalidIssuerException>();
    }

    [Fact]
    public async Task Token_without_expiry_is_rejected()
    {
        var token = _handler.CreateToken(Descriptor(ActiveCredentials(), hasExpiry: false));
        new JsonWebToken(token).TryGetPayloadValue<long>("exp", out _).ShouldBeFalse();

        var result = await ValidateAsync(token);

        result.IsValid.ShouldBeFalse();
        result.Exception.ShouldBeOfType<SecurityTokenNoExpirationException>();
    }

    [Fact]
    public async Task Token_expired_within_skew_is_accepted_and_beyond_skew_rejected()
    {
        var token = Issue();
        var expiresAt = Now.AddMinutes(10);

        _time.SetUtcNow(expiresAt.AddSeconds(30));
        var withinSkew = await ValidateAsync(token);

        _time.SetUtcNow(expiresAt.AddSeconds(31));
        var beyondSkew = await ValidateAsync(token);

        withinSkew.IsValid.ShouldBeTrue(withinSkew.Exception?.ToString());
        beyondSkew.IsValid.ShouldBeFalse();
        beyondSkew.Exception.ShouldBeOfType<SecurityTokenExpiredException>();
    }

    [Fact]
    public async Task Token_not_yet_valid_within_skew_is_accepted_and_beyond_skew_rejected()
    {
        var withinSkew = _handler.CreateToken(Descriptor(ActiveCredentials(), notBefore: Now.AddSeconds(30)));
        var beyondSkew = _handler.CreateToken(Descriptor(ActiveCredentials(), notBefore: Now.AddSeconds(31)));

        var accepted = await ValidateAsync(withinSkew);
        var rejected = await ValidateAsync(beyondSkew);

        accepted.IsValid.ShouldBeTrue(accepted.Exception?.ToString());
        rejected.IsValid.ShouldBeFalse();
        rejected.Exception.ShouldBeOfType<SecurityTokenNotYetValidException>();
    }

    [Fact]
    public async Task Token_signed_by_a_retired_key_still_validates()
    {
        // Signed before the rotation, while the retired key was active.
        var oldOptions = TestSigningKeys.Options(new JwtOptions.SigningKey { KeyId = "key-retired", PrivateKeyPem = _retiredPrivateKeyPem });
        var oldIssuer = new AccessTokenIssuer(TestSigningKeys.Provider(oldOptions), TestSigningKeys.Wrap(oldOptions), _time);
        var token = oldIssuer.Issue(Request).Value;

        var result = await ValidateAsync(token);

        result.IsValid.ShouldBeTrue(result.Exception?.ToString());
        new JsonWebToken(token).Kid.ShouldBe("key-retired");
    }

    private async Task<TokenValidationResult> ValidateAsync(string token)
    {
        var parameters = JwtValidation.CreateParameters(_options, _keys, _time);
        return await _handler.ValidateTokenAsync(token, parameters);
    }

    private string Issue() => new AccessTokenIssuer(_keys, TestSigningKeys.Wrap(_options), _time).Issue(Request).Value;

    private string Issue(SigningCredentials credentials) => _handler.CreateToken(Descriptor(credentials));

    private SigningCredentials ActiveCredentials() => _keys.Active;

    private static SecurityTokenDescriptor Descriptor(
        SigningCredentials? signingCredentials,
        string issuer = TestSigningKeys.Issuer,
        string audience = TestSigningKeys.Audience,
        bool hasExpiry = true,
        DateTimeOffset? notBefore = null) =>
        new()
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = Now.UtcDateTime,
            NotBefore = (notBefore ?? Now).UtcDateTime,
            Expires = hasExpiry ? Now.AddMinutes(10).UtcDateTime : null,
            SigningCredentials = signingCredentials,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = Request.UserId.ToString(),
                ["sid"] = Request.SessionId.ToString(),
                ["sst"] = Request.SecurityStamp,
                ["jti"] = Guid.NewGuid().ToString(),
            },
        };

    private static byte[] PublicKeyDer(string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return key.ExportSubjectPublicKeyInfo();
    }
}
