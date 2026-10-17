namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>The body of <c>POST /api/v1/auth/mfa/totp/confirm</c>: the 6-digit <c>code</c> the authenticator app shows.</summary>
internal sealed record ConfirmTotpRequest(string Code)
{
    /// <summary>Only the type name, so a logged or formatted request can never carry the code.</summary>
    public override string ToString() => nameof(ConfirmTotpRequest);
}
