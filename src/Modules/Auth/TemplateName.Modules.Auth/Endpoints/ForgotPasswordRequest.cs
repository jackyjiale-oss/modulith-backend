namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>The body of <c>POST /api/v1/auth/password/forgot</c>.</summary>
internal sealed record ForgotPasswordRequest(string Email);
