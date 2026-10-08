using System.Security.Cryptography;
using System.Text;
using TemplateName.Modules.Auth.Domain.Sessions.Events;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Sessions;

/// <summary>
/// One sign-in on one device. The session is also the refresh-token family: its tokens form a chain in which each token is used once
/// and replaced by the next. Its id is the <c>sid</c> claim of the access tokens. It keeps a snapshot of the user security stamp so a
/// password change or suspension ends it at the next refresh, and an absolute expiry that no token can outlive.
/// </summary>
internal sealed class UserSession : AggregateRoot<Guid>
{
    private readonly List<RefreshToken> _refreshTokens = [];

    // EF Core materializes the aggregate through this constructor; callers use Start.
    private UserSession()
    {
    }

    public Guid UserId { get; private set; }

    /// <summary>How the user proved their identity, as the <c>amr</c> values joined by a space (for example <c>pwd</c>).</summary>
    public string AuthMethods { get; private set; } = string.Empty;

    public string DeviceName { get; private set; } = string.Empty;

    public string? UserAgent { get; private set; }

    public string? IpAddress { get; private set; }

    /// <summary>The user security stamp at sign-in. A refresh with a different current stamp revokes the session.</summary>
    public string SecurityStamp { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    /// <summary>The absolute expiry; sliding refresh never extends it.</summary>
    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public SessionRevokedReason? RevokedReason { get; private set; }

    public IReadOnlyCollection<RefreshToken> RefreshTokens => _refreshTokens.AsReadOnly();

    /// <summary>Starts a session and its first refresh token (given as a hash). The session id is the future <c>sid</c> claim.</summary>
    public static (UserSession Session, RefreshToken FirstToken) Start(
        Guid userId,
        string authMethods,
        string deviceName,
        string? userAgent,
        string? ipAddress,
        string securityStamp,
        byte[] firstTokenHash,
        TimeSpan slidingLifetime,
        TimeSpan absoluteLifetime,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(slidingLifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(absoluteLifetime, TimeSpan.Zero);

        var session = new UserSession
        {
            Id = SequentialGuid.Create(now),
            UserId = userId,
            AuthMethods = authMethods,
            DeviceName = deviceName,
            UserAgent = userAgent,
            IpAddress = ipAddress,
            SecurityStamp = securityStamp,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + absoluteLifetime,
        };

        var first = session.IssueToken(firstTokenHash, slidingLifetime, now);

        return (session, first);
    }

    /// <summary>Active means neither revoked nor past the absolute expiry.</summary>
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    /// <summary>
    /// Exchanges the presented token for a new one. A failure can still change the session (a stamp mismatch or a reused token revokes
    /// it), so the caller saves the aggregate on every outcome; a hash that matches no token changes nothing.
    /// </summary>
    public Result<RefreshToken> Rotate(
        byte[] presentedTokenHash,
        byte[] newTokenHash,
        string currentSecurityStamp,
        TimeSpan slidingLifetime,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(slidingLifetime, TimeSpan.Zero);

        var presented = FindByHash(presentedTokenHash);
        if (presented is null || RevokedAt is not null)
        {
            return Result.Failure<RefreshToken>(SessionErrors.InvalidRefreshToken);
        }

        if (!StampMatches(currentSecurityStamp))
        {
            Revoke(SessionRevokedReason.PasswordChanged, now);

            return Result.Failure<RefreshToken>(SessionErrors.InvalidRefreshToken);
        }

        if (presented.UsedAt is not null || presented.RevokedAt is not null)
        {
            Revoke(SessionRevokedReason.TokenReuse, now);
            Raise(new RefreshTokenReuseDetectedDomainEvent(UserId, Id));

            return Result.Failure<RefreshToken>(SessionErrors.RefreshTokenReused);
        }

        if (now >= ExpiresAt || presented.IsExpired(now))
        {
            return Result.Failure<RefreshToken>(SessionErrors.RefreshTokenExpired);
        }

        var replacement = IssueToken(newTokenHash, slidingLifetime, now);
        presented.MarkUsed(replacement.Id, now);
        LastSeenAt = now;

        return replacement;
    }

    /// <summary>Ends the session and every token it still holds. Idempotent: a second call keeps the first time and reason.</summary>
    public void Revoke(SessionRevokedReason reason, DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokedReason = reason;
        foreach (var token in _refreshTokens)
        {
            token.Revoke(now);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private RefreshToken IssueToken(byte[] tokenHash, TimeSpan slidingLifetime, DateTimeOffset now)
    {
        var token = RefreshToken.Create(Id, tokenHash, Min(now + slidingLifetime, ExpiresAt), now);
        _refreshTokens.Add(token);

        return token;
    }

    // Every token is compared (no early exit) with a fixed-time comparison, so the time taken does not depend on which token matched.
    private RefreshToken? FindByHash(byte[] hash)
    {
        RefreshToken? match = null;
        foreach (var token in _refreshTokens)
        {
            if (CryptographicOperations.FixedTimeEquals(token.TokenHash, hash))
            {
                match = token;
            }
        }

        return match;
    }

    private bool StampMatches(string currentSecurityStamp) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(SecurityStamp), Encoding.UTF8.GetBytes(currentSecurityStamp));
}
