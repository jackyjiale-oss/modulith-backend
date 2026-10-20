using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Auth.Domain.Audit;
using TemplateName.Modules.Auth.Infrastructure.Persistence;

namespace TemplateName.Modules.Auth.Infrastructure.Retention;

/// <summary>
/// Deletes the audit log entries older (<c>OccurredAt</c>) than <c>Auth:Retention:AuditLogs</c>. Off by default: the entries are kept until an
/// archive exists to receive them, so an operator who sets a retention accepts that older entries are lost. Named
/// <c>AuthAuditLogsCleanup</c>, daily.
/// </summary>
internal sealed class AuditLogsCleanupJob(
    AuthDbContext context,
    TimeProvider timeProvider,
    IOptions<JobsOptions> jobs,
    IOptions<RetentionOptions> retention,
    IOptions<AuthRetentionOptions> authRetention)
    : BatchedCleanupJob(context, timeProvider, jobs, retention)
{
    internal const string JobName = "AuthAuditLogsCleanup";

    private const string DeleteSqlTemplate = "DELETE TOP (@Batch) FROM {table} WHERE OccurredAt < @Cutoff;";

    private readonly TimeSpan? _retentionPeriod = authRetention.Value.AuditLogs;
    private string? _deleteSql;

    public override string Name => JobName;

    public override TimeSpan DefaultInterval => TimeSpan.FromDays(1);

    protected override TimeSpan? RetentionPeriod => _retentionPeriod;

    protected override string DeleteSql => _deleteSql ??= DeleteSqlTemplate.Replace("{table}", DelimitedTable<AuthAuditLog>(), StringComparison.Ordinal);
}
