namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>The body of <c>POST /api/v1/auth/password/change</c>.</summary>
internal sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword)
{
    /// <summary>Only the type name, so a logged or formatted request can never carry either password.</summary>
    public override string ToString() => nameof(ChangePasswordRequest);
}
