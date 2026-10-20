using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.Modules.Auth.Infrastructure.Retention;

/// <summary>
/// Deletes the refresh tokens whose own <c>ExpiresAt</c> is more than <c>Auth:Retention:RefreshTokens</c> ago, whatever their session's state.
/// A token is deleted only once presenting it already fails for age, so the reuse detection (a used or revoked token presented inside its
/// lifetime revokes the session) is not weakened, and the unexpired tokens of a live session stay. Named <c>AuthRefreshTokensCleanup</c>, daily.
/// </summary>
internal sealed class RefreshTokensCleanupJob(
    AuthDbContext context,
    TimeProvider timeProvider,
    IOptions<JobsOptions> jobs,
    IOptions<RetentionOptions> retention,
    IOptions<AuthRetentionOptions> authRetention)
    : BatchedCleanupJob(context, timeProvider, jobs, retention)
{
    internal const string JobName = "AuthRefreshTokensCleanup";

    private const string DeleteSqlTemplate = "DELETE TOP (@Batch) FROM {table} WHERE ExpiresAt < @Cutoff;";

    private readonly TimeSpan? _retentionPeriod = authRetention.Value.RefreshTokens;
    private string? _deleteSql;

    public override string Name => JobName;

    public override TimeSpan DefaultInterval => TimeSpan.FromDays(1);

    protected override TimeSpan? RetentionPeriod => _retentionPeriod;

    protected override string DeleteSql => _deleteSql ??= DeleteSqlTemplate.Replace("{table}", DelimitedTable<RefreshToken>(), StringComparison.Ordinal);
}
