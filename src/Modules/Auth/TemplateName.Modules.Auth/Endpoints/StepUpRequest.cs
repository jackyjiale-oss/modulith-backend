namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>
/// The body of <c>POST /api/v1/auth/step-up</c>: exactly one of <c>password</c> (an account without two-step verification), <c>code</c>
/// (from the authenticator app) or <c>recoveryCode</c> (an account with it), or a social provider's proof (an account with neither a
/// password nor two-step verification): <c>provider</c> (its name, at most 64 characters) with an <c>idToken</c> and the raw
/// <c>nonce</c> it binds (16 to 256 characters), or, for Facebook only, an <c>accessToken</c>; each token at most 8192 characters.
/// </summary>
internal sealed record StepUpRequest(
    string? Password = null,
    string? Code = null,
    string? RecoveryCode = null,
    string? Provider = null,
    string? IdToken = null,
    string? AccessToken = null,
    string? Nonce = null)
{
    /// <summary>Only the type name, so a logged or formatted request can never carry the password, a code, a token or the nonce.</summary>
    public override string ToString() => nameof(StepUpRequest);
}
