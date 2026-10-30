namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>
/// The body of <c>POST /api/v1/auth/external/{provider}/login</c>: exactly one of <c>idToken</c> (with the raw <c>nonce</c> it binds,
/// 16 to 256 characters) and <c>accessToken</c> (Facebook only), each at most 8192 characters. <c>displayName</c> (at most 200) and
/// <c>locale</c> (a predefined culture; the request's language when absent) are used only when an account is created; without
/// <c>deviceName</c> (at most 200) the session is named <c>Unknown</c>.
/// </summary>
internal sealed record ExternalLoginRequest(
    string? IdToken = null,
    string? AccessToken = null,
    string? Nonce = null,
    string? DisplayName = null,
    string? Locale = null,
    string? DeviceName = null)
{
    /// <summary>Only the type name, so a logged or formatted request can never carry a token or the nonce.</summary>
    public override string ToString() => nameof(ExternalLoginRequest);
}
