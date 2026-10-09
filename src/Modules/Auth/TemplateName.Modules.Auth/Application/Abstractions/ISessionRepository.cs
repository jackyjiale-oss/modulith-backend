using TemplateName.Modules.Auth.Domain.Sessions;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>
/// Loads and adds <see cref="UserSession"/> aggregates. A session is always loaded with its whole refresh-token chain, because
/// <see cref="UserSession.Rotate"/> and <see cref="UserSession.Revoke"/> are only correct when every token is present.
/// </summary>
internal interface ISessionRepository
{
    Task<UserSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Finds the session that owns the token with this hash, used or not, with all of its tokens.</summary>
    Task<UserSession?> GetByRefreshTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken);

    /// <summary>The user's sessions that are neither revoked nor past their absolute expiry at <paramref name="now"/>.</summary>
    Task<IReadOnlyList<UserSession>> GetActiveByUserAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken);

    void Add(UserSession session);

    /// <summary>
    /// Marks the token used in one atomic statement, outside the change tracker. True only for the one caller whose statement changed
    /// the row; false when the token is unknown, already used or revoked. It neither loads nor changes a tracked session, so the caller
    /// loads the session first, claims, then rotates the instance it already holds.
    /// </summary>
    Task<bool> TryClaimRefreshTokenAsync(Guid refreshTokenId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// The token's <c>UsedAt</c> and <c>RevokedAt</c> as stored now, read without tracking, so a session the context already holds keeps
    /// its in-memory state; null when no such token exists. After a failed claim it tells a token used by another request (reuse) from
    /// one whose session was revoked meanwhile (an ended session).
    /// </summary>
    Task<RefreshTokenState?> GetRefreshTokenStateAsync(Guid refreshTokenId, CancellationToken cancellationToken);
}
