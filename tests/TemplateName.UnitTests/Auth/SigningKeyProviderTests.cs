using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Endpoints;
using TemplateName.Modules.Auth.Infrastructure.Tokens;

namespace TemplateName.UnitTests.Auth;

public sealed class SigningKeyProviderTests
{
    private const string Section = "Auth:Jwt:SigningKeys";

    [Fact]
    public void Defaults_match_the_plan()
    {
        var options = new JwtOptions();

        JwtOptions.SectionName.ShouldBe("Auth:Jwt");
        options.Audience.ShouldBe("templatename-api");
        options.AccessTokenLifetime.ShouldBe(TimeSpan.FromMinutes(10));
        options.ClockSkew.ShouldBe(TimeSpan.FromSeconds(30));
        options.SigningKeys.ShouldBeEmpty();
    }

    [Fact]
    public void Configured_keys_pass_validation_on_start()
    {
        var active = TestSigningKeys.PrivateKeyPem();
        var retired = TestSigningKeys.PrivateKeyPem();
        using var services = TestSigningKeys.Services("Production", new Dictionary<string, string?>
        {
            [$"{Section}:0:KeyId"] = "key-active",
            [$"{Section}:0:PrivateKeyPem"] = active,
            [$"{Section}:1:KeyId"] = "key-retired",
            [$"{Section}:1:PublicKeyPem"] = TestSigningKeys.PublicKeyPemOf(retired),
        });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());

        var keys = services.GetRequiredService<ISigningKeyProvider>();
        services.GetServices<IHostedService>().ShouldContain(keys as IHostedService);
        keys.Active.Key.KeyId.ShouldBe("key-active");
        keys.Active.Algorithm.ShouldBe(SecurityAlgorithms.EcdsaSha256);
        keys.ValidationKeys.Select(key => key.KeyId).ShouldBe(["key-active", "key-retired"]);
    }

    [Fact]
    public void Private_key_on_another_curve_is_rejected()
    {
        foreach (var curve in new[] { ECCurve.NamedCurves.nistP384, ECCurve.NamedCurves.nistP521, ECCurve.NamedCurves.brainpoolP256r1 })
        {
            var pem = TestSigningKeys.PrivateKeyPem(curve);
            using var services = TestSigningKeys.Services("Production", new Dictionary<string, string?>
            {
                [$"{Section}:0:KeyId"] = "key-wrong-curve",
                [$"{Section}:0:PrivateKeyPem"] = pem,
            });

            var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

            failure.Message.ShouldContain($"{Section}:0:PrivateKeyPem");
            failure.Message.ShouldContain("P-256");
            failure.Message.ShouldNotContain(Base64Body(pem));
        }
    }

    [Fact]
    public void Public_key_on_another_curve_is_rejected()
    {
        var retired = TestSigningKeys.PrivateKeyPem(ECCurve.NamedCurves.nistP384);

        var problems = Validate(TestSigningKeys.Active(), TestSigningKeys.Retired("key-retired", retired));

        problems.ShouldHaveSingleItem().ShouldContain($"{Section}:1:PublicKeyPem");
    }

    [Fact]
    public void Rsa_key_is_rejected()
    {
        using var rsa = RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();

        var problems = Validate(new JwtOptions.SigningKey { KeyId = "key-rsa", PrivateKeyPem = pem });

        problems.ShouldHaveSingleItem().ShouldContain($"{Section}:0:PrivateKeyPem");
        problems[0].ShouldNotContain(Base64Body(pem));
    }

    public static TheoryData<string> InvalidEntries() =>
    [
        "blank kid",
        "duplicate kid",
        "no pem",
        "garbage private pem",
        "public pem as private",
        "private pem as public",
        "mismatched pair",
        "no private key at all",
        "encrypted private pem",
    ];

    [Theory]
    [MemberData(nameof(InvalidEntries))]
    public void Invalid_entries_are_rejected_without_echoing_key_material(string invalidEntry)
    {
        var first = TestSigningKeys.PrivateKeyPem();
        var second = TestSigningKeys.PrivateKeyPem();
        using var encryptedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        JwtOptions.SigningKey[] keys = invalidEntry switch
        {
            "blank kid" => [new() { KeyId = " ", PrivateKeyPem = first }],
            "duplicate kid" => [new() { KeyId = "key-a", PrivateKeyPem = first }, new() { KeyId = "key-a", PublicKeyPem = TestSigningKeys.PublicKeyPemOf(second) }],
            "no pem" => [new() { KeyId = "key-a", PrivateKeyPem = first }, new() { KeyId = "key-b" }],
            "garbage private pem" => [new() { KeyId = "key-a", PrivateKeyPem = new string(PemEncoding.Write("PRIVATE KEY", "not a key"u8)) }],
            "public pem as private" => [new() { KeyId = "key-a", PrivateKeyPem = TestSigningKeys.PublicKeyPemOf(first) }],
            "private pem as public" => [new() { KeyId = "key-a", PrivateKeyPem = first }, new() { KeyId = "key-b", PublicKeyPem = second }],
            "mismatched pair" => [new() { KeyId = "key-a", PrivateKeyPem = first, PublicKeyPem = TestSigningKeys.PublicKeyPemOf(second) }],
            "no private key at all" => [new() { KeyId = "key-a", PublicKeyPem = TestSigningKeys.PublicKeyPemOf(first) }],
            "encrypted private pem" => [new() { KeyId = "key-a", PrivateKeyPem = EncryptedPem(encryptedKey) }],
            _ => throw new ArgumentOutOfRangeException(nameof(invalidEntry)),
        };

        var problems = Validate(keys);

        problems.ShouldNotBeEmpty();
        var text = string.Join(Environment.NewLine, problems);
        text.ShouldContain(Section);
        text.ShouldNotContain(Base64Body(first));
        text.ShouldNotContain(Base64Body(second));
        text.ShouldNotContain("PRIVATE KEY-----");
    }

    [Fact]
    public void Pem_without_line_breaks_is_accepted()
    {
        // An environment variable often loses the line breaks; the PEM is still one key.
        var singleLine = TestSigningKeys.PrivateKeyPem().ReplaceLineEndings(string.Empty);

        Validate(new JwtOptions.SigningKey { KeyId = "key-a", PrivateKeyPem = singleLine }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Missing_keys_outside_development_fail_validation_on_start(string environmentName)
    {
        using var services = TestSigningKeys.Services(environmentName);

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain(Section);
        Should.Throw<InvalidOperationException>(() => TestSigningKeys.Provider(new JwtOptions(), environmentName))
            .Message.ShouldContain(Section);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Ephemeral_key_is_used_in_development_and_testing(string environmentName)
    {
        using var services = TestSigningKeys.Services(environmentName);
        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        var logger = new CapturingLogger<SigningKeyProvider>();

        var keys = TestSigningKeys.Provider(new JwtOptions(), environmentName, logger);
        var other = TestSigningKeys.Provider(new JwtOptions(), environmentName);

        var key = keys.Active.Key.ShouldBeOfType<ECDsaSecurityKey>();
        key.ECDsa.ExportParameters(false).Curve.Oid.Value.ShouldBe(ECCurve.NamedCurves.nistP256.Oid.Value);
        key.KeyId.ShouldNotBeNullOrWhiteSpace();
        keys.Active.Algorithm.ShouldBe(SecurityAlgorithms.EcdsaSha256);
        keys.ValidationKeys.ShouldHaveSingleItem().KeyId.ShouldBe(key.KeyId);
        keys.PublicKeySet.Keys.ShouldHaveSingleItem().Kid.ShouldBe(key.KeyId);
        other.Active.Key.KeyId.ShouldNotBe(key.KeyId);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain(Section);
        entry.Message.ShouldContain(environmentName);
        entry.Message.ShouldNotContain("PRIVATE KEY");
        var parameters = key.ECDsa.ExportParameters(true);
        entry.Message.ShouldNotContain(Base64UrlEncoder.Encode(parameters.D));
        entry.Message.ShouldNotContain(Convert.ToBase64String(parameters.D!));
    }

    [Fact]
    public void First_entry_with_a_private_key_signs()
    {
        var retired = TestSigningKeys.PrivateKeyPem();
        var options = TestSigningKeys.Options(TestSigningKeys.Retired("key-retired", retired), TestSigningKeys.Active("key-active"), TestSigningKeys.Active("key-spare"));

        var keys = TestSigningKeys.Provider(options);

        keys.Active.Key.KeyId.ShouldBe("key-active");
        keys.ValidationKeys.Select(key => key.KeyId).ShouldBe(["key-retired", "key-active", "key-spare"]);
    }

    [Fact]
    public void Validation_keys_hold_no_private_part()
    {
        var keys = TestSigningKeys.Provider(TestSigningKeys.Options(TestSigningKeys.Active()));

        var key = keys.ValidationKeys.ShouldHaveSingleItem().ShouldBeOfType<ECDsaSecurityKey>();
        Should.Throw<CryptographicException>(() => key.ECDsa.ExportParameters(true));
    }

    [Fact]
    public void Jwks_publishes_only_public_parameters()
    {
        var activePem = TestSigningKeys.PrivateKeyPem();
        var retiredPem = TestSigningKeys.PrivateKeyPem();
        var options = TestSigningKeys.Options(
            new JwtOptions.SigningKey { KeyId = "key-active", PrivateKeyPem = activePem, PublicKeyPem = TestSigningKeys.PublicKeyPemOf(activePem) },
            new JwtOptions.SigningKey { KeyId = "key-retired", PrivateKeyPem = retiredPem });

        var keySet = TestSigningKeys.Provider(options).PublicKeySet;

        keySet.Keys.Select(key => key.Kid).ShouldBe(["key-active", "key-retired"]);
        foreach (var (key, pem) in keySet.Keys.Zip([activePem, retiredPem]))
        {
            key.Kty.ShouldBe("EC");
            key.Crv.ShouldBe("P-256");
            key.Alg.ShouldBe("ES256");
            key.Use.ShouldBe("sig");
            key.D.ShouldBeNullOrEmpty();
            key.HasPrivateKey.ShouldBeFalse();

            using var expected = ECDsa.Create();
            expected.ImportFromPem(pem);
            var point = expected.ExportParameters(false).Q;
            key.X.ShouldBe(Base64UrlEncoder.Encode(point.X));
            key.Y.ShouldBe(Base64UrlEncoder.Encode(point.Y));
        }

        // The body the endpoint writes: exactly the public members, never "d".
        var body = JsonSerializer.Serialize(JsonWebKeySetResponse.From(keySet), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(body);
        document.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(["keys"]);
        foreach (var key in document.RootElement.GetProperty("keys").EnumerateArray())
        {
            key.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ShouldBe(["alg", "crv", "kid", "kty", "use", "x", "y"]);
        }
    }

    [Fact]
    public async Task Bearer_is_the_default_scheme_and_uses_the_module_validation_and_clock()
    {
        // Years away from the real clock: the token validates only if the handler checks lifetime against the application's TimeProvider.
        var time = new FakeTimeProvider(new DateTimeOffset(2031, 3, 1, 9, 0, 0, TimeSpan.Zero));
        using var services = TestSigningKeys.Services("Testing", timeProvider: time);

        var schemes = services.GetRequiredService<IAuthenticationSchemeProvider>();
        (await schemes.GetDefaultAuthenticateSchemeAsync())!.Name.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
        (await schemes.GetDefaultChallengeSchemeAsync())!.Name.ShouldBe(JwtBearerDefaults.AuthenticationScheme);

        var bearer = services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        bearer.MapInboundClaims.ShouldBeFalse();
        var parameters = bearer.TokenValidationParameters;
        parameters.ValidAlgorithms.ShouldBe(["ES256"]);
        parameters.ValidIssuer.ShouldBe("templatename");
        parameters.ValidAudience.ShouldBe("templatename-api");
        parameters.TryAllIssuerSigningKeys.ShouldBeFalse();

        var token = services.GetRequiredService<IAccessTokenIssuer>()
            .Issue(new AccessTokenRequest(Guid.NewGuid(), Guid.NewGuid(), "STAMP", "pwd", time.GetUtcNow(), "en"));
        var handler = bearer.TokenHandlers.OfType<JsonWebTokenHandler>().ShouldHaveSingleItem();
        handler.MapInboundClaims.ShouldBeFalse();
        var result = await handler.ValidateTokenAsync(token.Value, parameters);
        result.IsValid.ShouldBeTrue(result.Exception?.ToString());
        result.ClaimsIdentity.FindFirst("sub").ShouldNotBeNull();
        result.ClaimsIdentity.Name.ShouldBe(result.ClaimsIdentity.FindFirst("sub")!.Value);

        time.Advance(TimeSpan.FromMinutes(11));
        (await handler.ValidateTokenAsync(token.Value, parameters)).Exception.ShouldBeOfType<SecurityTokenExpiredException>();
    }

    private static List<string> Validate(params JwtOptions.SigningKey[] keys)
    {
        var options = TestSigningKeys.Options(keys);
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            options,
            new System.ComponentModel.DataAnnotations.ValidationContext(options),
            results,
            validateAllProperties: true);
        return results.Select(result => result.ErrorMessage ?? string.Empty).ToList();
    }

    /// <summary>The base64 body of a PEM, one line, so a test can check that no part of the key leaked into a message.</summary>
    private static string Base64Body(string pem) =>
        string.Concat(pem.Split('\n').Where(line => !line.StartsWith("-----", StringComparison.Ordinal)).Select(line => line.Trim()))[..40];

    private static string EncryptedPem(ECDsa key) =>
        key.ExportEncryptedPkcs8PrivateKeyPem(
            "correct horse battery staple",
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception) + exception + string.Join(';', StateValues(state))));

        // The structured values too, so a key hidden in a property that the message does not print would still be caught.
        private static IEnumerable<string> StateValues(object? state) =>
            state is IEnumerable<KeyValuePair<string, object?>> values ? values.Select(pair => $"{pair.Key}={pair.Value}") : [];
    }
}
