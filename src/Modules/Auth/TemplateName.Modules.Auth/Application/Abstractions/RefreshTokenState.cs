namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>A refresh token's row as the database holds it now, read past the change tracker (<see cref="ISessionRepository.GetRefreshTokenStateAsync"/>).</summary>
/// <param name="UsedAt">Set once a refresh claimed the token.</param>
/// <param name="RevokedAt">Set once the token's session was revoked (logout, password change, administrator, reuse).</param>
internal sealed record RefreshTokenState(DateTimeOffset? UsedAt, DateTimeOffset? RevokedAt);
