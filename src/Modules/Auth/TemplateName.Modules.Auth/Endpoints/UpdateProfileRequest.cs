namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>The body of <c>PUT /api/v1/auth/me</c>.</summary>
/// <param name="DisplayName">The name to show; at most 200 characters.</param>
/// <param name="Locale">A predefined culture name, such as <c>en</c>, <c>ms</c> or <c>zh-Hans</c>.</param>
/// <param name="TimeZone">An IANA time zone id, such as <c>Asia/Kuala_Lumpur</c> or <c>UTC</c>.</param>
internal sealed record UpdateProfileRequest(string DisplayName, string Locale, string TimeZone);
