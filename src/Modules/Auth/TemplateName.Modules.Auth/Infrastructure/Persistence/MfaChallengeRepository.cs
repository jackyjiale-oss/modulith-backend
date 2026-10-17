using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.Modules.Auth.Infrastructure.Persistence;

internal sealed class MfaChallengeRepository(AuthDbContext context) : IMfaChallengeRepository
{
    // One statement, so simultaneous wrong codes queue on the row lock and each is counted; OUTPUT hands every caller its own new count.
    // Raw SQL because ExecuteUpdate cannot return it; it skips the change tracker. A consumed challenge is not matched.
    private const string RecordFailedAttemptSql = """
        UPDATE auth.MfaChallenges
        SET FailedAttempts = FailedAttempts + 1
        OUTPUT inserted.FailedAttempts
        WHERE Id = @Id
          AND ConsumedAt IS NULL
        """;

    public Task<MfaChallenge?> GetByTokenHashAsync(byte[] tokenHash, CancellationToken cancellationToken)
        => context.Set<MfaChallenge>().SingleOrDefaultAsync(challenge => challenge.TokenHash == tokenHash, cancellationToken);

    public void Add(MfaChallenge challenge) => context.Set<MfaChallenge>().Add(challenge);

    public async Task<int?> RecordFailedAttemptAsync(Guid challengeId, CancellationToken cancellationToken)
    {
        var rows = await context.Database
            .SqlQueryRaw<FailedAttemptRow>(
                RecordFailedAttemptSql,
                new SqlParameter("@Id", SqlDbType.UniqueIdentifier) { Value = challengeId })
            .ToListAsync(cancellationToken);

        return rows is [var row] ? row.FailedAttempts : null;
    }

    // The rule of MfaChallenge.CanUse in one UPDATE ... WHERE, without the stamp (the handler compares it with the loaded user's): the
    // database decides which of two simultaneous verifies wins. ExpiresAt is exclusive, as in the domain.
    public async Task<bool> TryConsumeAsync(Guid challengeId, DateTimeOffset now, int maxAttempts, CancellationToken cancellationToken)
    {
        var consumed = await context.Set<MfaChallenge>()
            .Where(challenge => challenge.Id == challengeId
                && challenge.ConsumedAt == null
                && challenge.ExpiresAt > now
                && challenge.FailedAttempts < maxAttempts)
            .ExecuteUpdateAsync(setters => setters.SetProperty(challenge => challenge.ConsumedAt, now), cancellationToken);

        return consumed == 1;
    }

    /// <summary>The <c>OUTPUT</c> row of <see cref="RecordFailedAttemptSql"/>.</summary>
    private sealed class FailedAttemptRow
    {
        public int FailedAttempts { get; init; }
    }
}
