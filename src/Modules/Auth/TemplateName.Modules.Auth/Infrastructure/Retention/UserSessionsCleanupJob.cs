using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Auth.Domain.Sessions;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.Modules.Auth.Infrastructure.Retention;

/// <summary>
/// Deletes the sessions that were revoked (<c>RevokedAt</c>) or expired (<c>ExpiresAt</c>) more than <c>Auth:Retention:Sessions</c> ago.
/// A session's refresh tokens go with it: the foreign key from <c>RefreshTokens.SessionId</c> cascades (see
/// <see cref="UserSessionConfiguration"/>), so the statement needs no second <c>DELETE</c> and the batch stays atomic. The reported count
/// is sessions, not tokens. Named <c>AuthSessionsCleanup</c>, daily.
/// </summary>
internal sealed class UserSessionsCleanupJob(
    AuthDbContext context,
    TimeProvider timeProvider,
    IOptions<JobsOptions> jobs,
    IOptions<RetentionOptions> retention,
    IOptions<AuthRetentionOptions> authRetention)
    : BatchedCleanupJob(context, timeProvider, jobs, retention)
{
    internal const string JobName = "AuthSessionsCleanup";

    private const string DeleteSqlTemplate = "DELETE TOP (@Batch) FROM {table} WHERE RevokedAt < @Cutoff OR ExpiresAt < @Cutoff;";

    private readonly TimeSpan? _retentionPeriod = authRetention.Value.Sessions;
    private string? _deleteSql;

    public override string Name => JobName;

    public override TimeSpan DefaultInterval => TimeSpan.FromDays(1);

    protected override TimeSpan? RetentionPeriod => _retentionPeriod;

    protected override string DeleteSql => _deleteSql ??= DeleteSqlTemplate.Replace("{table}", DelimitedTable<UserSession>(), StringComparison.Ordinal);
}
