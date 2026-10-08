using Microsoft.IdentityModel.Tokens;

namespace TemplateName.Modules.Auth.Infrastructure.Tokens;

/// <summary>The keys of <c>Auth:Jwt:SigningKeys</c>, loaded once at start-up.</summary>
internal interface ISigningKeyProvider
{
    /// <summary>The key that signs new access tokens (ES256), with its <c>kid</c>.</summary>
    SigningCredentials Active { get; }

    /// <summary>The public part of every configured key, active and retired, each with its <c>kid</c>.</summary>
    IReadOnlyCollection<SecurityKey> ValidationKeys { get; }

    /// <summary>The JWKS: the public parameters of <see cref="ValidationKeys"/>, never a private part.</summary>
    JsonWebKeySet PublicKeySet { get; }
}
