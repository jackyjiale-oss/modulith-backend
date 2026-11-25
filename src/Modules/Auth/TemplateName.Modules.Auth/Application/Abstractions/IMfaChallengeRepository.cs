using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>
/// Loads and adds <see cref="MfaChallenge"/> aggregates. The try counter and the consumption change only through the atomic
/// statements below, never through the change tracker, so simultaneous verifies of one challenge are each counted before their code is
/// checked and only one wins.
/// </summary>
internal interface IMfaChallengeRepository
{
    /// <summary>Finds a challenge by the SHA-256 hash of its token, whatever its state.</summary>
    Task<MfaChallenge?> GetByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken);

    void Add(MfaChallenge challenge);

    /// <summary>
    /// Claims one try of the challenge before the presented code is checked, by the rule of <see cref="MfaChallenge.CanUse"/> without the
    /// stamp, which the caller checks on the loaded user: one conditional <c>UPDATE ... SET FailedAttempts = FailedAttempts + 1 WHERE Id =
    /// @id AND ConsumedAt IS NULL AND ExpiresAt &gt; @now AND FailedAttempts &lt; @max</c>. False when the challenge is unknown, consumed,
    /// expired at <paramref name="now"/> or has all <paramref name="maxAttempts"/> tries claimed (also by requests running at the same
    /// moment), so parallel guesses never get more than <paramref name="maxAttempts"/> checks of one challenge. A tracked instance keeps
    /// its old values.
    /// </summary>
    Task<bool> TryClaimAttemptAsync(Guid challengeId, DateTimeOffset now, int maxAttempts, CancellationToken cancellationToken);

    /// <summary>
    /// Consumes the challenge in one atomic statement (<c>UPDATE ... SET ConsumedAt = @now WHERE Id = @id AND ConsumedAt IS NULL AND
    /// ExpiresAt &gt; @now</c>) for a caller that holds a claimed try (<see cref="TryClaimAttemptAsync"/>), which is where the cap is
    /// enforced. True only for the one caller whose statement changed the row; false when the challenge is unknown, consumed or expired
    /// at <paramref name="now"/>. A tracked instance keeps its old values.
    /// </summary>
    Task<bool> TryConsumeAsync(Guid challengeId, DateTimeOffset now, CancellationToken cancellationToken);
}
