using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>
/// Loads and adds <see cref="MfaChallenge"/> aggregates. The wrong-code counter and the consumption change only through the atomic
/// statements below, never through the change tracker, so simultaneous verifies of one challenge are each counted and only one wins.
/// </summary>
internal interface IMfaChallengeRepository
{
    /// <summary>Finds a challenge by the SHA-256 hash of its token, whatever its state.</summary>
    Task<MfaChallenge?> GetByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken);

    void Add(MfaChallenge challenge);

    /// <summary>
    /// Counts one wrong code in one atomic statement (<c>UPDATE ... SET FailedAttempts = FailedAttempts + 1 OUTPUT inserted.FailedAttempts
    /// WHERE Id = @id AND ConsumedAt IS NULL</c>) and returns the new count; null when nothing was counted (the challenge is unknown or
    /// already consumed). Simultaneous calls queue on the row lock, so each is counted once.
    /// </summary>
    Task<int?> RecordFailedAttemptAsync(Guid challengeId, CancellationToken cancellationToken);

    /// <summary>
    /// Consumes the challenge in one atomic statement by the rule of <see cref="MfaChallenge.CanUse"/> without the stamp, which the caller
    /// checks on the loaded user (<c>UPDATE ... SET ConsumedAt = @now WHERE Id = @id AND ConsumedAt IS NULL AND ExpiresAt &gt; @now AND
    /// FailedAttempts &lt; @max</c>). True only for the one caller whose statement changed the row; false when the challenge is unknown,
    /// consumed, expired at <paramref name="now"/> or has <paramref name="maxAttempts"/> wrong codes. A tracked instance keeps its old values.
    /// </summary>
    Task<bool> TryConsumeAsync(Guid challengeId, DateTimeOffset now, int maxAttempts, CancellationToken cancellationToken);
}
