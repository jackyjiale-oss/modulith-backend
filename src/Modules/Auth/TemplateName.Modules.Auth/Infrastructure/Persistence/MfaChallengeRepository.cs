using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class MfaChallengeRepository(AuthDbContext context) : IMfaChallengeRepository
{
    public Task<MfaChallenge?> GetByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken)
        => context.Set<MfaChallenge>().SingleOrDefaultAsync(challenge => challenge.TokenHash == tokenHash, cancellationToken);

    public void Add(MfaChallenge challenge) => context.Set<MfaChallenge>().Add(challenge);

    // One UPDATE ... WHERE: a try is claimed only while fewer than maxAttempts were, so simultaneous guesses queue on the row lock and at
    // most maxAttempts of them ever get their code checked. The rule of MfaChallenge.CanUse without the stamp (the handler compares it
    // with the loaded user's); ExpiresAt is exclusive, as in the domain.
    public async Task<bool> TryClaimAttemptAsync(Guid challengeId, DateTimeOffset now, int maxAttempts, CancellationToken cancellationToken)
    {
        var claimed = await context.Set<MfaChallenge>()
            .Where(challenge => challenge.Id == challengeId
                && challenge.ConsumedAt == null
                && challenge.ExpiresAt > now
                && challenge.FailedAttempts < maxAttempts)
            .ExecuteUpdateAsync(setters => setters.SetProperty(challenge => challenge.FailedAttempts, challenge => challenge.FailedAttempts + 1), cancellationToken);

        return claimed == 1;
    }

    // The caller holds a claimed try (its own, counted before it checked the code), so the cap is the claim's business, not this one's:
    // an unconsumed, unexpired challenge is consumed by exactly one caller.
    public async Task<bool> TryConsumeAsync(Guid challengeId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var consumed = await context.Set<MfaChallenge>()
            .Where(challenge => challenge.Id == challengeId
                && challenge.ConsumedAt == null
                && challenge.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(challenge => challenge.ConsumedAt, now), cancellationToken);

        return consumed == 1;
    }
}
