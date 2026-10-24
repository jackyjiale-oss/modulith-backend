using System.Globalization;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;

namespace TemplateName.Modules.Notifications.Infrastructure.Retention;

/// <summary>
/// Deletes the notifications created (<c>CreatedAt</c>) more than <c>Notifications:Retention:Notifications</c> ago that have no delivery
/// still <see cref="DeliveryStatus.Pending"/>: a pending delivery is still owed to the recipient, however old. The deliveries go with their
/// notification, dead-lettered ones included, so a dead letter stays for an administrator to find until the notification itself is that
/// old. They go by cascade (the foreign key from <c>Deliveries.NotificationId</c> cascades, see <see cref="NotificationConfiguration"/>), so
/// the statement needs no second <c>DELETE</c> and the batch stays atomic. The reported count is notifications, not deliveries. Named
/// <c>NotificationsRowsCleanup</c>, daily.
/// </summary>
internal sealed class NotificationsCleanupJob(
    NotificationsDbContext context,
    TimeProvider timeProvider,
    IOptions<JobsOptions> jobs,
    IOptions<RetentionOptions> retention,
    IOptions<NotificationsRetentionOptions> notificationsRetention)
    : BatchedCleanupJob(context, timeProvider, jobs, retention)
{
    internal const string JobName = "NotificationsRowsCleanup";

    private const string DeleteSqlTemplate =
        "DELETE TOP (@Batch) notification FROM {notifications} AS notification WHERE notification.CreatedAt < @Cutoff "
        + "AND NOT EXISTS (SELECT 1 FROM {deliveries} AS delivery WHERE delivery.NotificationId = notification.Id AND delivery.Status = {pending});";

    private readonly TimeSpan? _retentionPeriod = notificationsRetention.Value.Notifications;
    private string? _deleteSql;

    public override string Name => JobName;

    public override TimeSpan DefaultInterval => TimeSpan.FromDays(1);

    protected override TimeSpan? RetentionPeriod => _retentionPeriod;

    protected override string DeleteSql => _deleteSql ??= DeleteSqlTemplate
        .Replace("{notifications}", DelimitedTable<Notification>(), StringComparison.Ordinal)
        .Replace("{deliveries}", DelimitedTable<Delivery>(), StringComparison.Ordinal)
        .Replace("{pending}", ((byte)DeliveryStatus.Pending).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
