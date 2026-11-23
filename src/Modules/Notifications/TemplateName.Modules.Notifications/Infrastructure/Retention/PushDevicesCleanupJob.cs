using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Notifications.Domain.PushDevices;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;

namespace TemplateName.Modules.Notifications.Infrastructure.Retention;

/// <summary>
/// Deletes push devices deactivated more than <c>Notifications:Retention:PushDevices</c> (30 days) ago, daily. An active device stays
/// however old it is; a deactivated one is kept for a while so the administrator's delivery list can still name it.
/// </summary>
internal sealed class PushDevicesCleanupJob(
    NotificationsDbContext context,
    TimeProvider timeProvider,
    IOptions<JobsOptions> jobs,
    IOptions<RetentionOptions> retention,
    IOptions<NotificationsRetentionOptions> notificationsRetention)
    : BatchedCleanupJob(context, timeProvider, jobs, retention)
{
    internal const string JobName = "NotificationsPushDevicesCleanup";

    private const string DeleteSqlTemplate = "DELETE TOP (@Batch) FROM {table} WHERE DeactivatedAt < @Cutoff;";

    private readonly TimeSpan? _retentionPeriod = notificationsRetention.Value.PushDevices;

    private string? _deleteSql;

    public override string Name => JobName;

    public override TimeSpan DefaultInterval => TimeSpan.FromDays(1);

    protected override TimeSpan? RetentionPeriod => _retentionPeriod;

    protected override string DeleteSql => _deleteSql ??= DeleteSqlTemplate.Replace("{table}", DelimitedTable<PushDevice>(), StringComparison.Ordinal);
}
