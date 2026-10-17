using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.TrustedDevices;

/// <summary>
/// A device the user chose to remember after a second factor: it may skip the second factor at sign-in until it expires, is revoked, or
/// the security stamp of the user changes (a password change, an MFA change, a suspension or an admin reset forgets every device). The
/// token is opaque and only its SHA-256 hash is stored; the expiry is absolute, so using the device never extends it.
/// </summary>
internal sealed class TrustedDevice : AggregateRoot<Guid>
{
    /// <summary>The length of <see cref="TokenHash"/> in bytes (SHA-256).</summary>
    public const int HashLength = 32;

    // EF Core materializes the aggregate through this constructor; callers use Trust.
    private TrustedDevice()
    {
    }

    public Guid UserId { get; private set; }

    public byte[] TokenHash { get; private set; } = [];

    public string DeviceName { get; private set; } = string.Empty;

    public string? UserAgent { get; private set; }

    /// <summary>The security stamp of the user when the device was trusted.</summary>
    public string SecurityStamp { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>Trusts a device for <paramref name="lifetime"/>. A device name or user agent longer than its column is cut to it.</summary>
    /// <exception cref="ArgumentException"><paramref name="tokenHash"/> is not <see cref="HashLength"/> bytes, or the stamp is blank.</exception>
    public static TrustedDevice Trust(
        Guid userId,
        byte[] tokenHash,
        string deviceName,
        string? userAgent,
        string securityStamp,
        TimeSpan lifetime,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(securityStamp);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        if (tokenHash.Length != HashLength)
        {
            throw new ArgumentException($"A token hash is {HashLength} bytes.", nameof(tokenHash));
        }

        return new TrustedDevice
        {
            Id = SequentialGuid.Create(now),
            UserId = userId,
            TokenHash = tokenHash,
            DeviceName = UserSession.Cut(deviceName, UserSession.MaxDeviceNameLength),
            UserAgent = UserSession.Cut(userAgent, UserSession.MaxUserAgentLength),
            SecurityStamp = securityStamp,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
    }

    /// <summary>Usable while not revoked, <paramref name="now"/> is before <see cref="ExpiresAt"/> (exclusive) and the stamp equals the snapshot (ordinal comparison).</summary>
    public bool IsUsable(string currentSecurityStamp, DateTimeOffset now) =>
        RevokedAt is null
        && now < ExpiresAt
        && string.Equals(SecurityStamp, currentSecurityStamp, StringComparison.Ordinal);

    /// <summary>Records a sign-in through this device. The expiry stays as it is.</summary>
    public void MarkUsed(DateTimeOffset now) => LastUsedAt = now;

    /// <summary>Forgets the device. Idempotent: a second call keeps the first time.</summary>
    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
