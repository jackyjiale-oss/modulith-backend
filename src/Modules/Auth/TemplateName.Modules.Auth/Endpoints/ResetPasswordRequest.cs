namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>The body of <c>POST /api/v1/auth/password/reset</c>: the token from the emailed link and the new password.</summary>
internal sealed record ResetPasswordRequest(string Token, string NewPassword)
{
    /// <summary>Only the type name, so a logged or formatted request can never carry the token or the password.</summary>
    public override string ToString() => nameof(ResetPasswordRequest);
}
