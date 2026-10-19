namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>The body of <c>POST /api/v1/auth/mfa/totp/setup</c>; the body itself is optional, and so is <c>label</c> (<c>Authenticator app</c>).</summary>
internal sealed record SetUpTotpRequest(string? Label = null);
