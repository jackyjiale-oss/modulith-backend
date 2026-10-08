namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>The body of <c>POST /api/v1/auth/email/confirm</c>: the token from the emailed link.</summary>
internal sealed record ConfirmEmailRequest(string Token)
{
    /// <summary>Only the type name, so a logged or formatted request can never carry the token.</summary>
    public override string ToString() => nameof(ConfirmEmailRequest);
}
