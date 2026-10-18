using System.Text.Json.Serialization;

namespace TemplateName.Modules.Auth.Application.Mfa.Verify;

/// <summary>The tokens of the session a verified challenge starts. The refresh token is shown only here; the server keeps its hash.</summary>
/// <param name="AccessToken">The ES256 JWT to send as <c>Authorization: Bearer</c>; its <c>amr</c> is <c>pwd otp mfa</c> or <c>pwd rcvr mfa</c>.</param>
/// <param name="AccessTokenExpiresAt">The access token's <c>exp</c>.</param>
/// <param name="RefreshToken">The secret that gets the next token pair; 43 base64url characters, single use.</param>
/// <param name="RefreshTokenExpiresAt">When the refresh token stops working unless it is used first.</param>
/// <param name="SessionId">The session, also the access token's <c>sid</c> claim.</param>
/// <param name="TrustedDeviceToken">The token of a remembered device, shown once; always null until trusted devices arrive (a later Plan 4 task).</param>
/// <param name="TrustedDeviceExpiresAt">When the remembered device must verify again; null with <paramref name="TrustedDeviceToken"/>.</param>
internal sealed record MfaVerifyResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    Guid SessionId,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TrustedDeviceToken,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? TrustedDeviceExpiresAt)
{
    /// <summary>Only the type name, so a logged response can never carry a token.</summary>
    public override string ToString() => nameof(MfaVerifyResponse);
}
