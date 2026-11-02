using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Mfa;

/// <summary>
/// The state between a correct password and a correct second factor: an opaque token whose SHA-256 hash is stored, valid for a few
/// minutes, single use and bound to the security stamp of the user at the time of the password step (so a password change, an MFA
/// change or a suspension in between ends it). Only the verify route looks it up. The wrong-code counter and the consumption change
/// through conditional updates (<c>IMfaChallengeRepository</c>), so the aggregate only answers <see cref="CanUse"/> for what it has loaded.
/// </summary>
internal sealed class MfaChallenge : AggregateRoot<Guid>
{
    /// <summary>The length of <see cref="TokenHash"/> in bytes (SHA-256).</summary>
    public const int HashLength = 32;

    // EF Core materializes the aggregate through this constructor; callers use Issue.
    private MfaChallenge()
    {
    }

    public Guid UserId { get; private set; }

    public byte[] TokenHash { get; private set; } = [];

    /// <summary>The security stamp of the user when the challenge was issued.</summary>
    public string SecurityStamp { get; private set; } = string.Empty;

    /// <summary>The device name of the sign-in attempt, for the session or trusted device the verification creates.</summary>
    public string DeviceName { get; private set; } = string.Empty;

    /// <summary>
    /// How the user passed the first step: <see cref="AuthenticationMethods.Password"/> or <see cref="AuthenticationMethods.Federated"/>.
    /// The verification adds it to the <c>amr</c> of the session next to the second factor.
    /// </summary>
    public string FirstFactor { get; private set; } = AuthenticationMethods.Password;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary>The number of wrong codes presented for this challenge.</summary>
    public int FailedAttempts { get; private set; }

    /// <summary>Issues a challenge that expires <paramref name="lifetime"/> after <paramref name="now"/>. A device name longer than its column is cut to it.</summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="tokenHash"/> is not <see cref="HashLength"/> bytes, the stamp is blank, or <paramref name="firstFactor"/> is
    /// neither <see cref="AuthenticationMethods.Password"/> nor <see cref="AuthenticationMethods.Federated"/> (ordinal comparison).
    /// </exception>
    public static MfaChallenge Issue(
        Guid userId,
        byte[] tokenHash,
        string securityStamp,
        string deviceName,
        string firstFactor,
        TimeSpan lifetime,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(securityStamp);
        ArgumentException.ThrowIfNullOrWhiteSpace(firstFactor);
        if (firstFactor is not (AuthenticationMethods.Password or AuthenticationMethods.Federated))
        {
            throw new ArgumentException("The first factor is a password or a federated sign-in.", nameof(firstFactor));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        if (tokenHash.Length != HashLength)
        {
            throw new ArgumentException($"A token hash is {HashLength} bytes.", nameof(tokenHash));
        }

        return new MfaChallenge
        {
            Id = SequentialGuid.Create(now),
            UserId = userId,
            TokenHash = tokenHash,
            SecurityStamp = securityStamp,
            DeviceName = UserSession.Cut(deviceName, UserSession.MaxDeviceNameLength),
            FirstFactor = firstFactor,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
    }

    /// <summary>
    /// Whether a code may still be tried: not consumed, <paramref name="now"/> before <see cref="ExpiresAt"/> (exclusive), fewer than
    /// <paramref name="maxAttempts"/> wrong codes, and the <paramref name="currentSecurityStamp"/> of the user equal to the snapshot
    /// (ordinal comparison). Changes nothing; the caller consumes the challenge with one conditional update of the same rule.
    /// </summary>
    public bool CanUse(string currentSecurityStamp, DateTimeOffset now, int maxAttempts) =>
        ConsumedAt is null
        && now < ExpiresAt
        && FailedAttempts < maxAttempts
        && string.Equals(SecurityStamp, currentSecurityStamp, StringComparison.Ordinal);
}
