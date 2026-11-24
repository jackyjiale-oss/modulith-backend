namespace TemplateName.Modules.Auth.Application.Phone.RequestCode;

/// <summary>A code is on its way.</summary>
/// <param name="ExpiresAt">When the code stops working (UTC).</param>
internal sealed record PhoneCodeResponse(DateTimeOffset ExpiresAt);
