using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;

namespace TemplateName.Modules.Auth.Infrastructure.Tokens;

/// <summary>
/// Access token settings (section <c>Auth:Jwt</c>). <see cref="SigningKeys"/> is empty in the files; set it with user secrets, environment
/// variables or a secret store (ADR 0015). Every key is checked at start-up; a message names the entry, never its key material.
/// </summary>
internal sealed class JwtOptions : IValidatableObject
{
    internal const string SectionName = "Auth:Jwt";

    /// <summary>The configuration path of <see cref="SigningKeys"/>, used in every message about a key.</summary>
    internal const string SigningKeysPath = SectionName + ":" + nameof(SigningKeys);

    /// <summary>The <c>iss</c> claim written and required. Set it to the API's public address in a deployment.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Issuer { get; set; } = "templatename";

    /// <summary>The <c>aud</c> claim written and required.</summary>
    [Required(AllowEmptyStrings = false)]
    public string Audience { get; set; } = "templatename-api";

    /// <summary>How long an access token is valid. Short, because a token is not checked against session revocation (ADR 0015, D9).</summary>
    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The tolerance for clock differences when <c>exp</c> and <c>nbf</c> are checked.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:05:00")]
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The EC P-256 keys. The first entry with <see cref="SigningKey.PrivateKeyPem"/> signs; every entry validates and is published in the
    /// JWKS. Empty means an ephemeral key in Development and Testing, and a start-up failure everywhere else.
    /// </summary>
    public List<SigningKey> SigningKeys { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var keyIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < SigningKeys.Count; index++)
        {
            var entry = SigningKeys[index];
            var path = $"{SigningKeysPath}:{index}";

            if (string.IsNullOrWhiteSpace(entry.KeyId))
            {
                yield return Failure($"{path}:{nameof(SigningKey.KeyId)} is required.");
            }
            else if (!keyIds.Add(entry.KeyId))
            {
                yield return Failure($"{path}:{nameof(SigningKey.KeyId)} '{entry.KeyId}' is used by another key; key ids must be unique.");
            }

            if (string.IsNullOrWhiteSpace(entry.PrivateKeyPem) && string.IsNullOrWhiteSpace(entry.PublicKeyPem))
            {
                yield return Failure($"{path} has neither {nameof(SigningKey.PrivateKeyPem)} nor {nameof(SigningKey.PublicKeyPem)}.");
                continue;
            }

            ECDsa? privateKey = null;
            ECDsa? publicKey = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(entry.PrivateKeyPem)
                    && !SigningKeyProvider.TryLoadKey(entry.PrivateKeyPem, isPrivate: true, out privateKey, out var privateProblem))
                {
                    yield return Failure($"{path}:{nameof(SigningKey.PrivateKeyPem)} {privateProblem}");
                }

                if (!string.IsNullOrWhiteSpace(entry.PublicKeyPem)
                    && !SigningKeyProvider.TryLoadKey(entry.PublicKeyPem, isPrivate: false, out publicKey, out var publicProblem))
                {
                    yield return Failure($"{path}:{nameof(SigningKey.PublicKeyPem)} {publicProblem}");
                }

                if (privateKey is not null && publicKey is not null && !SigningKeyProvider.HaveSamePublicKey(privateKey, publicKey))
                {
                    yield return Failure($"{path}:{nameof(SigningKey.PublicKeyPem)} is not the public part of {nameof(SigningKey.PrivateKeyPem)}.");
                }
            }
            finally
            {
                privateKey?.Dispose();
                publicKey?.Dispose();
            }
        }

        if (SigningKeys.Count > 0 && SigningKeys.TrueForAll(entry => string.IsNullOrWhiteSpace(entry.PrivateKeyPem)))
        {
            yield return Failure($"{SigningKeysPath} has no entry with a {nameof(SigningKey.PrivateKeyPem)}, so no key can sign access tokens.");
        }
    }

    private static ValidationResult Failure(string message) => new(message, [nameof(SigningKeys)]);

    /// <summary>One entry of <c>Auth:Jwt:SigningKeys</c>. A retired key keeps only <see cref="PublicKeyPem"/>.</summary>
    internal sealed class SigningKey
    {
        /// <summary>The <c>kid</c> written in the token header and the JWKS. Required and unique.</summary>
        public string KeyId { get; set; } = string.Empty;

        /// <summary>The PEM private key (<c>PRIVATE KEY</c> or <c>EC PRIVATE KEY</c>, P-256). Secrets only, never in a file.</summary>
        public string? PrivateKeyPem { get; set; }

        /// <summary>The PEM public key (<c>PUBLIC KEY</c>, P-256). Optional next to a private key, which already holds it.</summary>
        public string? PublicKeyPem { get; set; }
    }
}
