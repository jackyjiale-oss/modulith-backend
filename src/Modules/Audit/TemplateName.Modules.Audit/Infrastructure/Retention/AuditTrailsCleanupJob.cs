using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Audit.Domain.Trails;
using TemplateName.Modules.Audit.Infrastructure.Persistence;

namespace TemplateName.Modules.Audit.Infrastructure.Retention;

/// <summary>
/// Deletes the audit trails older (<c>OccurredAt</c>) than <c>Audit:Retention:Trails</c>, in bounded batches (ADR 0028). Off by default:
/// the trails are kept until the operator sets a retention, and one who does accepts that older trails are lost unless archived first.
/// Named <c>AuditTrailsCleanup</c>, daily.
/// </summary>
internal sealed class AuditTrailsCleanupJob(
    AuditDbContext context,
    TimeProvider timeProvider,
    IOptions<JobsOptions> jobs,
    IOptions<RetentionOptions> retention,
    IOptions<AuditRetentionOptions> auditRetention)
    : BatchedCleanupJob(context, timeProvider, jobs, retention)
{
    internal const string JobName = "AuditTrailsCleanup";

    private const string DeleteSqlTemplate = "DELETE TOP (@Batch) FROM {table} WHERE OccurredAt < @Cutoff;";

    private readonly TimeSpan? _retentionPeriod = auditRetention.Value.Trails;
    private string? _deleteSql;

    public override string Name => JobName;

    public override TimeSpan DefaultInterval => TimeSpan.FromDays(1);

    protected override TimeSpan? RetentionPeriod => _retentionPeriod;

    protected override string DeleteSql => _deleteSql ??= DeleteSqlTemplate.Replace("{table}", DelimitedTable<AuditTrail>(), StringComparison.Ordinal);
}
