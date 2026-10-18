namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>
/// The body of <c>POST /api/v1/auth/mfa/verify</c>: the <c>challengeToken</c> login answered with and exactly one of <c>code</c> (6 digits
/// from the authenticator app) or <c>recoveryCode</c>. <c>rememberDevice</c> is accepted and ignored until trusted devices arrive.
/// </summary>
internal sealed record VerifyMfaRequest(string ChallengeToken, string? Code = null, string? RecoveryCode = null, bool? RememberDevice = null)
{
    /// <summary>Only the type name, so a logged or formatted request can never carry the challenge or a code.</summary>
    public override string ToString() => nameof(VerifyMfaRequest);
}
