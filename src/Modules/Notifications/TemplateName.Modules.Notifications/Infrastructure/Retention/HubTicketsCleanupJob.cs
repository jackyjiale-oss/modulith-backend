using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Notifications.Domain.HubTickets;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;

namespace TemplateName.Modules.Notifications.Infrastructure.Retention;

/// <summary>
/// Deletes the hub tickets that expired (<c>ExpiresAt</c>) more than <c>Notifications:Retention:HubTickets</c> ago. A ticket lives seconds,
/// so its expiry covers a consumed ticket as well: consumption is not a reason to delete it, and a ticket that has not expired stays.
/// Named <c>NotificationsHubTicketsCleanup</c>, hourly.
/// </summary>
internal sealed class HubTicketsCleanupJob(
    NotificationsDbContext context,
    TimeProvider timeProvider,
    IOptions<JobsOptions> jobs,
    IOptions<RetentionOptions> retention,
    IOptions<NotificationsRetentionOptions> notificationsRetention)
    : BatchedCleanupJob(context, timeProvider, jobs, retention)
{
    internal const string JobName = "NotificationsHubTicketsCleanup";

    private const string DeleteSqlTemplate = "DELETE TOP (@Batch) FROM {table} WHERE ExpiresAt < @Cutoff;";

    private readonly TimeSpan? _retentionPeriod = notificationsRetention.Value.HubTickets;
    private string? _deleteSql;

    public override string Name => JobName;

    public override TimeSpan DefaultInterval => TimeSpan.FromHours(1);

    protected override TimeSpan? RetentionPeriod => _retentionPeriod;

    protected override string DeleteSql => _deleteSql ??= DeleteSqlTemplate.Replace("{table}", DelimitedTable<HubTicket>(), StringComparison.Ordinal);
}
