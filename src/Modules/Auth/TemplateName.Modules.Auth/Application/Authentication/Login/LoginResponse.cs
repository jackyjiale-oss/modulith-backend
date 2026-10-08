namespace TemplateName.Modules.Auth.Application.Authentication.Login;

/// <summary>The tokens of a new session. The refresh token is shown only here; the server keeps its hash.</summary>
/// <param name="AccessToken">The ES256 JWT to send as <c>Authorization: Bearer</c>.</param>
/// <param name="AccessTokenExpiresAt">The access token's <c>exp</c>.</param>
/// <param name="RefreshToken">The secret that gets the next token pair; 43 base64url characters, single use.</param>
/// <param name="RefreshTokenExpiresAt">When the refresh token stops working unless it is used first.</param>
/// <param name="SessionId">The session, also the access token's <c>sid</c> claim.</param>
internal sealed record LoginResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    Guid SessionId)
{
    /// <summary>Only the type name and the session, so a logged response can never carry a token.</summary>
    public override string ToString() => $"{nameof(LoginResponse)} {{ SessionId = {SessionId} }}";
}
