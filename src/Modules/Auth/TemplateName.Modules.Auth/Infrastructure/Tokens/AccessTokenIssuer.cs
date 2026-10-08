using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Infrastructure.Tokens;

/// <summary>
/// Signs ES256 access tokens with the active key. The claims are <c>sub</c>, <c>sid</c>, <c>sst</c>, <c>amr</c> (array), <c>auth_time</c>,
/// <c>locale</c>, <c>jti</c>, <c>iat</c>, <c>nbf</c>, <c>exp</c>, <c>iss</c> and <c>aud</c>: no permission list and no personal data.
/// Times come from <see cref="TimeProvider"/>, truncated to whole seconds so <see cref="AccessToken.ExpiresAt"/> equals <c>exp</c>.
/// </summary>
internal sealed class AccessTokenIssuer(ISigningKeyProvider keys, IOptions<JwtOptions> options, TimeProvider timeProvider) : IAccessTokenIssuer
{
    // The descriptor sets every time claim itself; the handler's own defaults would read the system clock.
    private readonly JsonWebTokenHandler _handler = new() { SetDefaultTimesOnTokenCreation = false };

    public AccessToken Issue(AccessTokenRequest request)
    {
        var settings = options.Value;
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds((issuedAt + settings.AccessTokenLifetime).ToUnixTimeSeconds());

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = keys.Active,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = request.UserId.ToString(),
                [JwtRegisteredClaimNames.Sid] = request.SessionId.ToString(),
                ["sst"] = request.SecurityStamp,
                [JwtRegisteredClaimNames.Amr] = request.AuthMethods.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                [JwtRegisteredClaimNames.AuthTime] = request.AuthTime.ToUnixTimeSeconds(),
                ["locale"] = request.Locale,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture),
            },
        };

        return new AccessToken(_handler.CreateToken(descriptor), expiresAt);
    }
}
