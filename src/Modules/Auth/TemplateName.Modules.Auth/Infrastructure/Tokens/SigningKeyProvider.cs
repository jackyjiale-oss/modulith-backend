using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace TemplateName.Modules.Auth.Infrastructure.Tokens;

/// <summary>
/// Loads <c>Auth:Jwt:SigningKeys</c> once (EC P-256 only). The first entry with a private key signs; every entry validates and is published,
/// through a public-only copy, so neither the validation keys nor the JWKS can carry a private part. With no key configured it generates
/// one ephemeral key in Development and Testing and refuses to start anywhere else. Key material is never logged or put in a message.
/// It is also registered as a hosted service that does nothing, so the host builds it, and loads the keys, when it starts, not on the
/// first request that needs them.
/// </summary>
internal sealed partial class SigningKeyProvider : ISigningKeyProvider, IHostedService, IDisposable
{
    /// <summary>The environment the integration tests run in (<c>WebApplicationFactory</c>); like Development, it may use an ephemeral key.</summary>
    internal const string TestingEnvironmentName = "Testing";

    internal const string MissingKeysMessage =
        JwtOptions.SigningKeysPath + " is empty. Configure at least one EC P-256 signing key with user secrets, environment variables or a"
        + " secret store (docs/modules/auth.md); an ephemeral key is generated only in the Development and Testing environments.";

    /// <summary>The object identifier of the NIST P-256 curve (secp256r1, prime256v1), the only curve ES256 allows.</summary>
    private const string P256Oid = "1.2.840.10045.3.1.7";

    private readonly List<ECDsa> _ownedKeys = [];

    public SigningKeyProvider(IOptions<JwtOptions> options, IHostEnvironment environment, ILogger<SigningKeyProvider> logger)
    {
        var entries = options.Value.SigningKeys;
        var validationKeys = new List<SecurityKey>();
        var publicKeySet = new JsonWebKeySet();
        SigningCredentials? active = null;

        if (entries.Count == 0)
        {
            if (!AllowsEphemeralKey(environment))
            {
                throw new InvalidOperationException(MissingKeysMessage);
            }

            var keyId = "ephemeral-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            var key = Own(ECDsa.Create(ECCurve.NamedCurves.nistP256));
            active = Credentials(key, keyId);
            Publish(key, keyId, validationKeys, publicKeySet);
            LogEphemeralKey(logger, JwtOptions.SigningKeysPath, keyId, environment.EnvironmentName);
        }

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var path = $"{JwtOptions.SigningKeysPath}:{index}";
            ECDsa key;
            if (!string.IsNullOrWhiteSpace(entry.PrivateKeyPem))
            {
                key = Load(entry.PrivateKeyPem, isPrivate: true, $"{path}:{nameof(JwtOptions.SigningKey.PrivateKeyPem)}");
                active ??= Credentials(key, entry.KeyId);
            }
            else if (!string.IsNullOrWhiteSpace(entry.PublicKeyPem))
            {
                key = Load(entry.PublicKeyPem, isPrivate: false, $"{path}:{nameof(JwtOptions.SigningKey.PublicKeyPem)}");
            }
            else
            {
                throw new InvalidOperationException($"{path} has neither a private nor a public key.");
            }

            Publish(key, entry.KeyId, validationKeys, publicKeySet);
        }

        Active = active ?? throw new InvalidOperationException($"{JwtOptions.SigningKeysPath} has no entry with a private key, so no key can sign access tokens.");
        ValidationKeys = validationKeys.AsReadOnly();
        PublicKeySet = publicKeySet;
    }

    public SigningCredentials Active { get; }

    public IReadOnlyCollection<SecurityKey> ValidationKeys { get; }

    public JsonWebKeySet PublicKeySet { get; }

    /// <summary>Whether <paramref name="environment"/> may run on a key generated at start (Development and Testing only).</summary>
    internal static bool AllowsEphemeralKey(IHostEnvironment environment) =>
        environment.IsDevelopment() || environment.IsEnvironment(TestingEnvironmentName);

    /// <summary>
    /// Reads one PEM key: a <c>PRIVATE KEY</c> or <c>EC PRIVATE KEY</c> block when <paramref name="isPrivate"/>, otherwise a <c>PUBLIC KEY</c>
    /// block, on P-256. <paramref name="problem"/> completes a sentence that starts with the configuration path; it never quotes the PEM.
    /// </summary>
    internal static bool TryLoadKey(string pem, bool isPrivate, [NotNullWhen(true)] out ECDsa? key, [NotNullWhen(false)] out string? problem)
    {
        key = null;
        var expected = isPrivate ? "a PEM 'PRIVATE KEY' or 'EC PRIVATE KEY' block" : "a PEM 'PUBLIC KEY' block";
        if (!PemEncoding.TryFind(pem, out var fields))
        {
            problem = $"is not {expected}.";
            return false;
        }

        var label = pem.AsSpan()[fields.Label];
        var hasExpectedLabel = isPrivate ? label is "PRIVATE KEY" or "EC PRIVATE KEY" : label is "PUBLIC KEY";
        if (!hasExpectedLabel)
        {
            problem = $"is not {expected}.";
            return false;
        }

        var candidate = ECDsa.Create();
        try
        {
            candidate.ImportFromPem(pem);
            var curve = candidate.ExportParameters(includePrivateParameters: false).Curve;
            if (!IsP256(curve))
            {
                problem = $"is not on the P-256 curve that ES256 requires (found {CurveName(curve)}).";
                candidate.Dispose();
                return false;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            // The exception text is not passed on: it is not needed to fix the configuration and is never allowed to carry key bytes.
            problem = $"is not {expected} holding a single EC key.";
            candidate.Dispose();
            return false;
        }

        key = candidate;
        problem = null;
        return true;
    }

    /// <summary>Whether two keys have the same public point.</summary>
    internal static bool HaveSamePublicKey(ECDsa first, ECDsa second)
    {
        var firstPoint = first.ExportParameters(includePrivateParameters: false).Q;
        var secondPoint = second.ExportParameters(includePrivateParameters: false).Q;
        return firstPoint.X.AsSpan().SequenceEqual(secondPoint.X) && firstPoint.Y.AsSpan().SequenceEqual(secondPoint.Y);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        foreach (var key in _ownedKeys)
        {
            key.Dispose();
        }

        _ownedKeys.Clear();
    }

    private static bool IsP256(ECCurve curve) =>
        curve.IsNamed && (curve.Oid.Value == P256Oid || curve.Oid.FriendlyName is "nistP256" or "ECDSA_P256");

    private static string CurveName(ECCurve curve) =>
        curve.IsNamed ? curve.Oid.FriendlyName ?? curve.Oid.Value ?? "an unnamed curve" : "explicit curve parameters";

    private static SigningCredentials Credentials(ECDsa key, string keyId) =>
        new(new ECDsaSecurityKey(key) { KeyId = keyId }, SecurityAlgorithms.EcdsaSha256);

    private ECDsa Load(string pem, bool isPrivate, string path)
    {
        // Options validation has already refused a bad key at start; this guards a provider built without it.
        if (!TryLoadKey(pem, isPrivate, out var key, out var problem))
        {
            throw new InvalidOperationException($"{path} {problem}");
        }

        return Own(key);
    }

    /// <summary>Adds the public part of <paramref name="key"/> to the validation keys and the JWKS, through a copy that has no private part.</summary>
    private void Publish(ECDsa key, string keyId, List<SecurityKey> validationKeys, JsonWebKeySet publicKeySet)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var publicKey = Own(ECDsa.Create(parameters));
        validationKeys.Add(new ECDsaSecurityKey(publicKey) { KeyId = keyId });
        publicKeySet.Keys.Add(new JsonWebKey
        {
            Kty = JsonWebAlgorithmsKeyTypes.EllipticCurve,
            Crv = JsonWebKeyECTypes.P256,
            X = Base64UrlEncoder.Encode(parameters.Q.X),
            Y = Base64UrlEncoder.Encode(parameters.Q.Y),
            Kid = keyId,
            Alg = SecurityAlgorithms.EcdsaSha256,
            Use = JsonWebKeyUseNames.Sig,
        });
    }

    private ECDsa Own(ECDsa key)
    {
        _ownedKeys.Add(key);
        return key;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "{SigningKeysPath} is empty, so the ephemeral signing key {KeyId} was generated for the {EnvironmentName} environment; tokens it signs stop validating when the process restarts")]
    private static partial void LogEphemeralKey(ILogger logger, string signingKeysPath, string keyId, string environmentName);
}
