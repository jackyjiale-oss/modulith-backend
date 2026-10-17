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
    /// <summary>The column limit of <see cref="CreatedIp"/> (long enough for IPv6 with an IPv4 tail).</summary>
    public const int MaxCreatedIpLength = 45;

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

    /// <summary>
    /// Issues a code and raises <see cref="VerificationCodeIssuedDomainEvent"/> carrying the protected token for the email and the
    /// <paramref name="trigger"/>, which only the event keeps. An address longer than <see cref="MaxCreatedIpLength"/> is cut to it.
    /// </summary>
    /// <exception cref="ArgumentException">An administrator <paramref name="trigger"/> for a code that is not a password reset.</exception>
    public static VerificationCode Issue(
        Guid userId,
        VerificationPurpose purpose,
        VerificationTrigger trigger,
        string target,
        byte[] tokenHash,
        string protectedToken,
        TimeSpan lifetime,
        string? createdIp,
        DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        if (trigger != VerificationTrigger.SelfService && purpose != VerificationPurpose.PasswordReset)
        {
            throw new ArgumentException($"Only a password reset can be issued by an administrator, not {purpose}.", nameof(trigger));
        }

        var code = new VerificationCode
        {
            Id = SequentialGuid.Create(now),
            UserId = userId,
            Purpose = purpose,
            Target = target,
            TokenHash = tokenHash,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
            CreatedIp = createdIp is { Length: > MaxCreatedIpLength } ip ? ip[..MaxCreatedIpLength] : createdIp,
        };

        code.Raise(new VerificationCodeIssuedDomainEvent(code.Id, userId, purpose, target, protectedToken, trigger));

        return code;
    }

    /// <summary>A code is pending until it is consumed, invalidated or reaches <see cref="ExpiresAt"/> (exclusive).</summary>
    public bool IsPending(DateTimeOffset now) => ConsumedAt is null && InvalidatedAt is null && now < ExpiresAt;

    /// <summary>
    /// Whether <see cref="Consume"/> would succeed: the code is for <paramref name="expected"/> and still pending. Changes nothing, so a
    /// handler can check the code before it claims it in the database (<c>IVerificationCodeRepository.TryConsumeAsync</c>, the same rule
    /// in one conditional statement) and then consumes the instance it holds.
    /// </summary>
    public bool CanConsume(VerificationPurpose expected, DateTimeOffset now) => Purpose == expected && IsPending(now);

    /// <summary>Uses the code once. Every refusal is the same <see cref="VerificationErrors.InvalidToken"/> so the cause stays private.</summary>
    public Result Consume(VerificationPurpose expected, DateTimeOffset now)
    {
        if (!CanConsume(expected, now))
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
