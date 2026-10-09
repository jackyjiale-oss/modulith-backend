namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>The body of <c>POST /api/v1/auth/register</c>. Without <c>locale</c>, the request's language is saved.</summary>
internal sealed record RegisterRequest(string Email, string Password, string DisplayName, string? Locale = null)
{
    /// <summary>Only the type name, so a logged or formatted request can never carry the password.</summary>
    public override string ToString() => nameof(RegisterRequest);
}
