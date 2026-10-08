namespace TemplateName.Modules.Auth.Application.Sessions.List;

/// <summary>One active sign-in of the caller. Never a token, a token hash or the security stamp.</summary>
/// <param name="Id">The session id, the <c>sid</c> claim of its access tokens; the id <c>DELETE auth/sessions/{id}</c> takes.</param>
/// <param name="DeviceName">The name the client gave at sign-in, <c>Unknown</c> when it gave none.</param>
/// <param name="IpAddress">The client address at sign-in, if known.</param>
/// <param name="UserAgent">The <c>User-Agent</c> at sign-in, if sent.</param>
/// <param name="CreatedAt">When the user signed in (UTC).</param>
/// <param name="LastSeenAt">When the session last refreshed its tokens (UTC); the sign-in time until the first refresh.</param>
/// <param name="ExpiresAt">The absolute expiry (UTC); refreshing never extends it.</param>
/// <param name="IsCurrent">Whether this is the session the request's access token belongs to.</param>
internal sealed record SessionResponse(
    Guid Id,
    string DeviceName,
    string? IpAddress,
    string? UserAgent,
    DateTime CreatedAt,
    DateTime LastSeenAt,
    DateTime ExpiresAt,
    bool IsCurrent);
