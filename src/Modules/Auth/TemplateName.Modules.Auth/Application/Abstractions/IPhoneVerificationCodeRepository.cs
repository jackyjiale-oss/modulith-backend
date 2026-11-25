using TemplateName.Modules.Auth.Domain.Verification;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>
/// Loads and adds <see cref="PhoneVerificationCode"/> aggregates. A code is pending while it is neither consumed nor invalidated and
/// <c>now &lt; ExpiresAt</c>, the rule of <see cref="PhoneVerificationCode.IsPending"/>. The two <c>Try</c> methods are single conditional
/// statements, so the database decides a race; they bypass the change tracker. A code is never compared before its try is claimed.
/// </summary>
internal interface IPhoneVerificationCodeRepository
{
    void Add(PhoneVerificationCode code);

    /// <summary>Finds a code by its id, whatever its state.</summary>
    Task<PhoneVerificationCode?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The user's newest pending code of <paramref name="purpose"/>, or null.</summary>
    Task<PhoneVerificationCode?> GetPendingAsync(Guid userId, PhoneCodePurpose purpose, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Every pending code of the user of <paramref name="purpose"/>, or of both purposes when it is null, tracked, so a handler can
    /// invalidate them.
    /// </summary>
    Task<IReadOnlyList<PhoneVerificationCode>> ListPendingAsync(
        Guid userId,
        PhoneCodePurpose? purpose,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>When the user's newest code was issued, whatever its state, or null when there is none (the resend cooldown).</summary>
    Task<DateTimeOffset?> GetLastIssuedAtAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// When each code the user was issued at or after <paramref name="since"/> was issued, oldest first (the daily cap, and when the
    /// oldest of them leaves the window).
    /// </summary>
    Task<IReadOnlyList<DateTimeOffset>> ListIssuedSinceAsync(Guid userId, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>
    /// Claims one try of a pending code before the presented code is compared with it: one conditional <c>UPDATE</c> that raises
    /// <c>AttemptCount</c> only while it is below <paramref name="maxAttempts"/>. False when the tries are used up (also by requests
    /// running at the same moment) or the code is no longer pending, so parallel guesses never get more than
    /// <paramref name="maxAttempts"/> comparisons of one code.
    /// </summary>
    Task<bool> TryClaimAttemptAsync(Guid id, int maxAttempts, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Consumes a pending code; of concurrent callers exactly one gets true.</summary>
    Task<bool> TryConsumeAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);
}
