using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TemplateName.Modules.Auth.Domain.Verification.Events;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.Verification;

/// <summary>
/// A 6-digit code texted to a phone number the user wants to verify. Only a salted SHA-256 hash of the code is stored; the code travels
/// only encrypted, inside the requested event. A code ends when it is consumed, when a newer code replaces it, when it reaches the
/// try limit (every try is claimed in the database before the code is compared,
/// <c>IPhoneVerificationCodeRepository.TryClaimAttemptAsync</c>, and a code whose tries are used up compares nothing) or when it expires.
/// </summary>
internal sealed class PhoneVerificationCode : AggregateRoot<Guid>
{
    /// <summary>The number of digits in a code.</summary>
    public const int CodeLength = 6;

    /// <summary>The length of <see cref="Salt"/> in bytes.</summary>
    public const int SaltLength = 16;

    // EF Core materializes the aggregate through this constructor; callers use Issue.
    private PhoneVerificationCode()
    {
    }

    public Guid UserId { get; private set; }

    public PhoneCodePurpose Purpose { get; private set; }


    /// <summary>The pending number in E.164 that the code was sent to; it becomes the user's number when the code is confirmed.</summary>
    public string PhoneNumber { get; private set; } = string.Empty;

    public byte[] CodeHash { get; private set; } = [];

    public byte[] Salt { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>The tries claimed so far (the right code's too); the repository claims them in one conditional statement, so this instance may lag behind.</summary>
    public int AttemptCount { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    /// <summary>Set when a newer code replaced this one or the number was removed or changed. A code whose tries are used up is not marked: it is refused by its claim.</summary>
    public DateTimeOffset? InvalidatedAt { get; private set; }

    /// <summary>A uniformly random code of <see cref="CodeLength"/> digits, leading zeros kept.</summary>
    public static string NewCode() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    /// <summary>
    /// Issues a code of <paramref name="purpose"/> for <paramref name="phoneNumber"/> (E.164), keeping only a hash of
    /// <paramref name="code"/> with a new salt, and raises <see cref="PhoneVerificationRequestedDomainEvent"/> (to verify a number) or
    /// <see cref="PhoneSignInCodeIssuedDomainEvent"/> (to sign in) carrying <paramref name="protectedCode"/> (the code encrypted by the
    /// application), which only the event keeps.
    /// </summary>
    public static PhoneVerificationCode Issue(
        Guid userId,
        PhoneCodePurpose purpose,
        string phoneNumber,
        string code,
        string protectedCode,
        TimeSpan lifetime,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(protectedCode);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        var salt = RandomNumberGenerator.GetBytes(SaltLength);

        var issued = new PhoneVerificationCode
        {
            Id = SequentialGuid.Create(now),
            UserId = userId,
            Purpose = purpose,
            PhoneNumber = phoneNumber,
            Salt = salt,
            CodeHash = Hash(salt, code),
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };

        issued.Raise(purpose switch
        {
            PhoneCodePurpose.Verify => new PhoneVerificationRequestedDomainEvent(issued.Id, userId, phoneNumber, protectedCode, issued.ExpiresAt),
            PhoneCodePurpose.SignIn => new PhoneSignInCodeIssuedDomainEvent(issued.Id, userId, phoneNumber, protectedCode, issued.ExpiresAt),
            _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "Unknown phone code purpose."),
        });

        return issued;
    }

    /// <summary>Whether <paramref name="code"/> is the issued code, compared in constant time.</summary>
    /// <summary>
    /// Spends what <see cref="Matches"/> costs, with a fresh salt, when there is no code to compare with, so a request for an unknown
    /// number or one with no pending code takes as long as one with a wrong code.
    /// </summary>
    public static void SpendMatchCost(string code) =>
        _ = CryptographicOperations.FixedTimeEquals(Hash(RandomNumberGenerator.GetBytes(SaltLength), code ?? string.Empty), new byte[32]);

    public bool Matches(string code) =>
        code is not null && CryptographicOperations.FixedTimeEquals(Hash(Salt, code), CodeHash);

    /// <summary>A code is pending until it is consumed, invalidated or reaches <see cref="ExpiresAt"/> (exclusive).</summary>
    public bool IsPending(DateTimeOffset now) => ConsumedAt is null && InvalidatedAt is null && now < ExpiresAt;

    /// <summary>Makes a pending code unusable. Idempotent: a consumed or already invalidated code keeps its state.</summary>
    public void Invalidate(DateTimeOffset now)
    {
        if (ConsumedAt is not null || InvalidatedAt is not null)
        {
            return;
        }

        InvalidatedAt = now;
    }

    /// <summary>
    /// Records the code as used on this instance, after the repository has claimed it in the database (<c>TryConsumeAsync</c>), so the
    /// two stay in step.
    /// </summary>
    public void Consume(DateTimeOffset now) => ConsumedAt ??= now;

    /// <summary>Names the code by its ids only: never the code, its hash or the number.</summary>
    public override string ToString() => $"PhoneVerificationCode {{ Id = {Id}, UserId = {UserId} }}";

    private static byte[] Hash(byte[] salt, string code)
    {
        var codeBytes = Encoding.UTF8.GetBytes(code);
        var input = new byte[salt.Length + codeBytes.Length];
        salt.CopyTo(input, 0);
        codeBytes.CopyTo(input, salt.Length);

        return SHA256.HashData(input);
    }
}
