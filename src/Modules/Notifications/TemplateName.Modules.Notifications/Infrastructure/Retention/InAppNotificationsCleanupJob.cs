using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.Modules.Notifications.Domain.InApp;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;

namespace TemplateName.Modules.Notifications.Infrastructure.Retention;

/// <summary>
/// Deletes the inbox rows created (<c>CreatedAt</c>) more than <c>Notifications:Retention:InAppNotifications</c> ago, read or unread. A row
/// refers to its notification by id only (no foreign key), so this is its own statement and clock: it neither waits for the notification
/// to go nor takes it along. Named <c>NotificationsInAppCleanup</c>, daily.
/// </summary>
internal sealed class InAppNotificationsCleanupJob(
    NotificationsDbContext context,
    TimeProvider timeProvider,
    IOptions<JobsOptions> jobs,
    IOptions<RetentionOptions> retention,
    IOptions<NotificationsRetentionOptions> notificationsRetention)
    : BatchedCleanupJob(context, timeProvider, jobs, retention)
{
    internal const string JobName = "NotificationsInAppCleanup";

    private const string DeleteSqlTemplate = "DELETE TOP (@Batch) FROM {table} WHERE CreatedAt < @Cutoff;";

    private readonly TimeSpan? _retentionPeriod = notificationsRetention.Value.InAppNotifications;
    private string? _deleteSql;

    public override string Name => JobName;

    public override TimeSpan DefaultInterval => TimeSpan.FromDays(1);

    protected override TimeSpan? RetentionPeriod => _retentionPeriod;

    protected override string DeleteSql => _deleteSql ??= DeleteSqlTemplate.Replace("{table}", DelimitedTable<InAppNotification>(), StringComparison.Ordinal);
}
