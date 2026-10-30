namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>
/// The body of <c>POST /api/v1/auth/external/{provider}/link</c>: exactly one of <c>idToken</c> (with the raw <c>nonce</c> it binds,
/// 16 to 256 characters) and <c>accessToken</c> (Facebook only), each at most 8192 characters.
/// </summary>
internal sealed record LinkExternalLoginRequest(string? IdToken = null, string? AccessToken = null, string? Nonce = null)
{
    /// <summary>Only the type name, so a logged or formatted request can never carry a token or the nonce.</summary>
    public override string ToString() => nameof(LinkExternalLoginRequest);
}
