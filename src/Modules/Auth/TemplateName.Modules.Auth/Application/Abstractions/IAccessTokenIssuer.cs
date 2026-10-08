namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Issues the short-lived access token (an ES256 JWT) for a session. The token carries no permission list and no personal data.</summary>
internal interface IAccessTokenIssuer
{
    /// <summary>Signs a token with the active key; it expires <c>Auth:Jwt:AccessTokenLifetime</c> after now.</summary>
    AccessToken Issue(AccessTokenRequest request);
}

/// <summary>What an access token says about its session.</summary>
/// <param name="UserId">The <c>sub</c> claim.</param>
/// <param name="SessionId">The <c>sid</c> claim: the <c>UserSession</c> the token belongs to.</param>
/// <param name="SecurityStamp">The <c>sst</c> claim: the session's snapshot of the user's security stamp.</param>
/// <param name="AuthMethods">The <c>amr</c> values joined by a space (for example <c>pwd</c>); the token carries them as a JSON array.</param>
/// <param name="AuthTime">The <c>auth_time</c> claim: when the user signed in, not when this token was issued.</param>
/// <param name="Locale">The <c>locale</c> claim: the user's saved language.</param>
internal sealed record AccessTokenRequest(Guid UserId, Guid SessionId, string SecurityStamp, string AuthMethods, DateTimeOffset AuthTime, string Locale);

/// <summary>A signed access token.</summary>
/// <param name="Value">The compact JWT.</param>
/// <param name="ExpiresAt">The instant of its <c>exp</c> claim (whole seconds).</param>
internal sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);
