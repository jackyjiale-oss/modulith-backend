namespace TemplateName.Modules.Notifications.Domain.HubTickets;

/// <summary>
/// A single-use credential for opening the notification hub connection (D12, ADR 0021). Only the SHA-256 hash of the ticket is stored,
/// so a database or log leak yields nothing usable; the key is that hash. The database claims a ticket with one conditional update
/// (<c>IHubTicketRepository.TryConsumeAsync</c>, the same rule as <see cref="CanConsume"/>), which is what makes two concurrent callers
/// safe.
/// </summary>
internal sealed class HubTicket
{
    /// <summary>The length of a SHA-256 hash, and so of <see cref="TokenHash"/>.</summary>
    public const int TokenHashLength = 32;

    // EF Core materializes the entity through this constructor; callers use Issue.
    private HubTicket()
    {
    }

    public byte[] TokenHash { get; private set; } = [];

    public Guid UserId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <exception cref="ArgumentException"><paramref name="tokenHash"/> is not 32 bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="lifetime"/> is not positive.</exception>
    public static HubTicket Issue(Guid userId, byte[] tokenHash, TimeSpan lifetime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        if (tokenHash.Length != TokenHashLength)
        {
            throw new ArgumentException($"A ticket hash is {TokenHashLength} bytes (SHA-256), not {tokenHash.Length}.", nameof(tokenHash));
        }

        return new HubTicket
        {
            TokenHash = tokenHash,
            UserId = userId,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
    }

    /// <summary>A ticket can be consumed once, while <paramref name="now"/> is before <see cref="ExpiresAt"/> (exclusive).</summary>
    public bool CanConsume(DateTimeOffset now) => ConsumedAt is null && now < ExpiresAt;

    /// <summary>Consumes the ticket when <see cref="CanConsume"/>; returns whether it did.</summary>
    public bool TryConsume(DateTimeOffset now)
    {
        if (!CanConsume(now))
        {
            return false;
        }

        ConsumedAt = now;

        return true;
    }
}
