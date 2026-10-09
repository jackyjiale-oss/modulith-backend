using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Sessions;

/// <summary>One link in a session chain of refresh tokens. Only the SHA-256 hash of the token is stored; the token itself is shown to the client once.</summary>
internal sealed class RefreshToken : Entity<Guid>
{
    // EF Core materializes the entity through this constructor; the session creates tokens.
    private RefreshToken()
    {
    }

    public Guid SessionId { get; private set; }

    public byte[] TokenHash { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>When the token was exchanged for its replacement. A used token that is presented again signals theft.</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    public Guid? ReplacedByTokenId { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    internal static RefreshToken Create(Guid sessionId, byte[] tokenHash, DateTimeOffset expiresAt, DateTimeOffset now) => new()
    {
        Id = SequentialGuid.Create(now),
        SessionId = sessionId,
        TokenHash = tokenHash,
        CreatedAt = now,
        ExpiresAt = expiresAt,
    };

    internal bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    internal void MarkUsed(Guid replacementId, DateTimeOffset now)
    {
        UsedAt = now;
        ReplacedByTokenId = replacementId;
    }

    internal void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
