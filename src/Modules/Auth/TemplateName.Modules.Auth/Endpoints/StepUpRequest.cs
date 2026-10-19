namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>
/// The body of <c>POST /api/v1/auth/step-up</c>: exactly one of <c>password</c> (an account without two-step verification), <c>code</c>
/// (from the authenticator app) or <c>recoveryCode</c> (an account with it).
/// </summary>
internal sealed record StepUpRequest(string? Password = null, string? Code = null, string? RecoveryCode = null)
{
    /// <summary>Only the type name, so a logged or formatted request can never carry the password or a code.</summary>
    public override string ToString() => nameof(StepUpRequest);
}
