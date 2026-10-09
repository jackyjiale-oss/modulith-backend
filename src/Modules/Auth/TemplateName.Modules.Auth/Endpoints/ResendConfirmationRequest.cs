namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>The body of <c>POST /api/v1/auth/email/resend-confirmation</c>.</summary>
internal sealed record ResendConfirmationRequest(string Email);
