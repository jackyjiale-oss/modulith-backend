namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>The body of <c>POST /api/v1/auth/token/refresh</c>.</summary>
internal sealed record RefreshTokenRequest(string RefreshToken)
{
    /// <summary>Only the type name, so a logged or formatted request can never carry the token.</summary>
    public override string ToString() => nameof(RefreshTokenRequest);
}
