namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>The body of <c>POST /api/v1/auth/login</c>. Without <c>deviceName</c>, the session is named <c>Unknown</c>.</summary>
internal sealed record LoginRequest(string Email, string Password, string? DeviceName = null)
{
    /// <summary>Only the type name, so a logged or formatted request can never carry the password.</summary>
    public override string ToString() => nameof(LoginRequest);
}
