using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TemplateName.Modules.Auth.Infrastructure.Tokens;

/// <summary>The one definition of what an acceptable access token is; the bearer handler and the tests use it.</summary>
internal static class JwtValidation
{
    /// <summary>
    /// ES256 only (so <c>none</c>, <c>HS256</c> and every other algorithm fail), the configured issuer and audience, a required <c>exp</c>
    /// and <c>nbf</c> checked with <see cref="JwtOptions.ClockSkew"/> against <paramref name="timeProvider"/> (the system clock when null),
    /// and a signature from the key the header's <c>kid</c> names. An unknown or missing <c>kid</c> resolves no key and fails; other keys
    /// are never tried.
    /// </summary>
    internal static TokenValidationParameters CreateParameters(JwtOptions options, ISigningKeyProvider keys, TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var keysById = keys.ValidationKeys
            .Where(key => !string.IsNullOrEmpty(key.KeyId))
            .ToDictionary(key => key.KeyId, StringComparer.Ordinal);

        return new TokenValidationParameters
        {
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = options.ClockSkew,

            // IdentityModel reads the system clock and its TimeProvider is internal, so lifetime is checked here against the application's
            // TimeProvider, with the library's rules and exception types.
            LifetimeValidator = (notBefore, expires, _, parameters) => ValidateLifetime(notBefore, expires, parameters.ClockSkew, clock),
            IssuerSigningKeyResolver = (_, _, keyId, _) =>
                !string.IsNullOrEmpty(keyId) && keysById.TryGetValue(keyId, out var key) ? [key] : [],
            TryAllIssuerSigningKeys = false,
            NameClaimType = JwtRegisteredClaimNames.Sub,
        };
    }

    /// <summary>
    /// A token is valid from <c>nbf - skew</c> up to and including <c>exp + skew</c>; <c>exp</c> is required. Throws the exception the library
    /// would throw, so the bearer handler reports the same reason.
    /// </summary>
    private static bool ValidateLifetime(DateTime? notBefore, DateTime? expires, TimeSpan clockSkew, TimeProvider clock)
    {
        if (expires is null)
        {
            throw new SecurityTokenNoExpirationException("The token has no expiration time.");
        }

        if (notBefore is not null && notBefore.Value > expires.Value)
        {
            throw new SecurityTokenInvalidLifetimeException("The token's not-before time is after its expiration time.")
            {
                NotBefore = notBefore,
                Expires = expires,
            };
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (notBefore is not null && notBefore.Value > now + clockSkew)
        {
            throw new SecurityTokenNotYetValidException("The token is not valid yet.") { NotBefore = notBefore.Value };
        }

        if (expires.Value < now - clockSkew)
        {
            throw new SecurityTokenExpiredException("The token has expired.") { Expires = expires.Value };
        }

        return true;
    }
}
