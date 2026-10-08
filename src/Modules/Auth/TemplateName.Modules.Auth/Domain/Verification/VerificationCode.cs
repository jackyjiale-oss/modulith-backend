using TemplateName.Modules.Auth.Domain.Verification.Events;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Verification;

/// <summary>
/// A single-use code behind an emailed link (email confirmation or password reset). Only the SHA-256 hash of the token is stored; the
/// repository looks the code up by that unique hash, so the domain never compares hashes. The token itself travels only inside the
/// issued event, encrypted.
/// </summary>
internal sealed class VerificationCode : AggregateRoot<Guid>
{
    // EF Core materializes the aggregate through this constructor; callers use Issue.
    private VerificationCode()
    {
    }

    public Guid UserId { get; private set; }

    public VerificationPurpose Purpose { get; private set; }

    /// <summary>The normalized email address the link was sent to.</summary>
    public string Target { get; private set; } = string.Empty;

    public byte[] TokenHash { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary>Set when a newer code replaced this one or the account state made it useless.</summary>
    public DateTimeOffset? InvalidatedAt { get; private set; }

    public string? CreatedIp { get; private set; }

    /// <summary>Issues a code and raises <see cref="VerificationCodeIssuedDomainEvent"/> carrying the protected token for the email.</summary>
    public static VerificationCode Issue(
        Guid userId,
        VerificationPurpose purpose,
        string target,
        byte[] tokenHash,
        string protectedToken,
        TimeSpan lifetime,
        string? createdIp,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        var code = new VerificationCode
        {
            Id = SequentialGuid.Create(now),
            UserId = userId,
            Purpose = purpose,
            Target = target,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
            CreatedIp = createdIp,
        };

        code.Raise(new VerificationCodeIssuedDomainEvent(code.Id, userId, purpose, target, protectedToken));

        return code;
    }

    /// <summary>A code is pending until it is consumed, invalidated or reaches <see cref="ExpiresAt"/> (exclusive).</summary>
    public bool IsPending(DateTimeOffset now) => ConsumedAt is null && InvalidatedAt is null && now < ExpiresAt;

    /// <summary>Uses the code once. Every refusal is the same <see cref="VerificationErrors.InvalidToken"/> so the cause stays private.</summary>
    public Result Consume(VerificationPurpose expected, DateTimeOffset now)
    {
        if (Purpose != expected || !IsPending(now))
        {
            return Result.Failure(VerificationErrors.InvalidToken);
        }

        ConsumedAt = now;

        return Result.Success();
    }

    /// <summary>Makes a pending code unusable. Idempotent: a consumed or already invalidated code keeps its state.</summary>
    public void Invalidate(DateTimeOffset now)
    {
        if (ConsumedAt is not null || InvalidatedAt is not null)
        {
            return;
        }

        InvalidatedAt = now;
    }
}
