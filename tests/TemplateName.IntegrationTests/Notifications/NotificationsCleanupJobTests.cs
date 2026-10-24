using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Persistence;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.HubTickets;
using TemplateName.Modules.Notifications.Domain.InApp;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;
using TemplateName.Modules.Notifications.Infrastructure.Retention;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary>
/// The Notifications cleanup jobs (ADR 0028): for each table the rows either side of the cutoff (a row goes only when strictly older),
/// that a notification takes its deliveries with it but stays while one is still pending, that the in-app rows go on their own clock, that
/// a hub ticket goes after it expires whether or not it was consumed, that each job reads its own key, and that each predicate uses its index.
/// </summary>
public sealed class NotificationsCleanupJobTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string TypeCode = "auth.password_changed";

    private static readonly TimeSpan OneHour = TimeSpan.FromHours(1);
    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);
    private static readonly TimeSpan OneEightyDays = TimeSpan.FromDays(180);
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromSeconds(30);

    private static readonly string[] JobNames =
    [
        HubTicketsCleanupJob.JobName,
        NotificationsCleanupJob.JobName,
        InAppNotificationsCleanupJob.JobName,
    ];

    private static readonly TimeSpan[] AgeGrid =
    [
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromDays(3),
        TimeSpan.FromDays(20),
        TimeSpan.FromDays(90),
        TimeSpan.FromDays(200),
        TimeSpan.FromDays(400),
    ];

    private readonly Guid _userId = Guid.NewGuid();

    public static TheoryData<string> AllJobNames => [.. JobNames];

    public static TheoryData<string, bool> AllJobNamesWithOffValues
    {
        get
        {
            var data = new TheoryData<string, bool>();
            foreach (var name in JobNames)
            {
                data.Add(name, true);
                data.Add(name, false);
            }

            return data;
        }
    }

    private DateTimeOffset Now => Factory.Time.GetUtcNow();

    [Fact]
    public async Task The_three_notification_cleanup_jobs_are_registered_with_their_intervals()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetServices<RecurringJobRegistration>()
            .Select(registration => (IRecurringJob)scope.ServiceProvider.GetRequiredService(registration.JobType))
            .Where(job => JobNames.Contains(job.Name, StringComparer.Ordinal))
            .ToDictionary(job => job.Name, job => job.DefaultInterval);

        jobs.ShouldBe(
            new Dictionary<string, TimeSpan>
            {
                ["NotificationsHubTicketsCleanup"] = OneHour,
                ["NotificationsRowsCleanup"] = OneDay,
                ["NotificationsInAppCleanup"] = OneDay,
            },
            ignoreOrder: true);
    }

    [Fact]
    public async Task The_scheduler_runs_a_cleanup_job_registered_by_the_Notifications_module()
    {
        var ticket = await SeedTicketAsync(Now - OneHour - OneSecond);
        var runner = Factory.Services.GetRequiredService<RecurringJobRunner>();

        (await runner.RunDueAsync(name => name == HubTicketsCleanupJob.JobName, Ct)).ShouldBe(1);

        (await TicketKeysAsync()).ShouldNotContain(ticket);
    }

    [Fact]
    public async Task Hub_tickets_expired_more_than_one_hour_ago_go_and_a_ticket_expired_exactly_one_hour_ago_stays()
    {
        var exactly = await SeedTicketAsync(Now - OneHour);
        var justOver = await SeedTicketAsync(Now - OneHour - OneSecond);
        var live = await SeedTicketAsync(Now + TicketLifetime);

        var result = await RunAsync(HubTicketsCleanupJob.JobName);

        result.Processed.ShouldBe(1);
        var remaining = await TicketKeysAsync();
        remaining.ShouldBe([exactly, live], ignoreOrder: true);
        remaining.ShouldNotContain(justOver);
    }

    [Fact]
    public async Task A_consumed_ticket_goes_after_it_expires_not_at_consumption()
    {
        // Issued now for thirty seconds and consumed at once: consumption is no reason to delete it, expiry is.
        var consumed = await SeedTicketAsync(Now + TicketLifetime, consumed: true);
        var unused = await SeedTicketAsync(Now + TicketLifetime);

        (await RunAsync(HubTicketsCleanupJob.JobName)).Processed.ShouldBe(0);
        (await TicketKeysAsync()).ShouldBe([consumed, unused], ignoreOrder: true);

        // An hour after it expired is still inside the retention; a second later it is out.
        Factory.Time.Advance(TicketLifetime + OneHour);
        (await RunAsync(HubTicketsCleanupJob.JobName)).Processed.ShouldBe(0);
        (await TicketKeysAsync()).ShouldBe([consumed, unused], ignoreOrder: true);

        Factory.Time.Advance(OneSecond);
        (await RunAsync(HubTicketsCleanupJob.JobName)).Processed.ShouldBe(2);
        (await TicketKeysAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_ticket_still_valid_stays()
    {
        var live = await SeedTicketAsync(Now + TicketLifetime);
        var consumedLive = await SeedTicketAsync(Now + TicketLifetime, consumed: true);

        // Even with the shortest retention a ticket that has not expired is not touched.
        var result = await RunAsync(HubTicketsCleanupJob.JobName, new NotificationsRetentionOptions { HubTickets = OneSecond });

        result.Processed.ShouldBe(0);
        (await TicketKeysAsync()).ShouldBe([live, consumedLive], ignoreOrder: true);
    }

    [Fact]
    public async Task Notifications_older_than_one_hundred_and_eighty_days_go_and_one_exactly_that_old_stays()
    {
        var exactly = await SeedNotificationAsync(Now - OneEightyDays, DeliveryStatus.Sent);
        var justOver = await SeedNotificationAsync(Now - OneEightyDays - OneSecond, DeliveryStatus.Sent);
        var recent = await SeedNotificationAsync(Now - OneDay, DeliveryStatus.Sent);

        var result = await RunAsync(NotificationsCleanupJob.JobName);

        result.Processed.ShouldBe(1);
        var remaining = await NotificationIdsAsync();
        remaining.ShouldBe([exactly, recent], ignoreOrder: true);
        remaining.ShouldNotContain(justOver);
    }

    [Fact]
    public async Task Deliveries_go_with_their_notification()
    {
        var old = await SeedNotificationAsync(Now - OneEightyDays - OneDay, DeliveryStatus.Sent, DeliveryStatus.Expired);
        var recent = await SeedNotificationAsync(Now - OneDay, DeliveryStatus.Sent, DeliveryStatus.Sent);

        var result = await RunAsync(NotificationsCleanupJob.JobName);

        // The result counts notifications, not the deliveries that went by cascade.
        result.Processed.ShouldBe(1);
        (await NotificationIdsAsync()).ShouldBe([recent]);
        var remaining = await DeliveryOwnersAsync();
        remaining.Count.ShouldBe(2);
        remaining.ShouldAllBe(owner => owner == recent);
        remaining.ShouldNotContain(old);
    }

    [Fact]
    public async Task A_notification_with_a_pending_delivery_stays()
    {
        var oneOfTwoPending = await SeedNotificationAsync(Now - TimeSpan.FromDays(400), DeliveryStatus.Sent, DeliveryStatus.Pending);
        var allPending = await SeedNotificationAsync(Now - TimeSpan.FromDays(400), DeliveryStatus.Pending, DeliveryStatus.Pending);
        await SeedNotificationAsync(Now - TimeSpan.FromDays(400), DeliveryStatus.Sent, DeliveryStatus.Expired);

        (await RunAsync(NotificationsCleanupJob.JobName)).Processed.ShouldBe(1);

        (await NotificationIdsAsync()).ShouldBe([oneOfTwoPending, allPending], ignoreOrder: true);
        (await DeliveryOwnersAsync()).Count.ShouldBe(4);

        // Once the worker settles the pending ones the notification is no different from any other old one.
        await SettlePendingDeliveriesAsync();
        (await RunAsync(NotificationsCleanupJob.JobName)).Processed.ShouldBe(2);

        (await NotificationIdsAsync()).ShouldBeEmpty();
        (await DeliveryOwnersAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task Dead_lettered_deliveries_go_with_a_180_day_old_notification_not_before()
    {
        var recent = await SeedNotificationAsync(Now - TimeSpan.FromDays(10), DeliveryStatus.DeadLettered, DeliveryStatus.Sent);
        var nearly = await SeedNotificationAsync(Now - OneEightyDays, DeliveryStatus.DeadLettered);
        var old = await SeedNotificationAsync(Now - OneEightyDays - OneSecond, DeliveryStatus.DeadLettered, DeliveryStatus.DeadLettered);

        (await RunAsync(NotificationsCleanupJob.JobName)).Processed.ShouldBe(1);

        (await NotificationIdsAsync()).ShouldBe([recent, nearly], ignoreOrder: true);
        var remaining = await DeliveryOwnersAsync();
        remaining.Count.ShouldBe(3);
        remaining.ShouldNotContain(old);
        (await DeadLetteredDeliveryCountAsync()).ShouldBe(2);

        // A day on, the 180-day-old one goes too.
        Factory.Time.Advance(OneDay);
        (await RunAsync(NotificationsCleanupJob.JobName)).Processed.ShouldBe(1);
        (await NotificationIdsAsync()).ShouldBe([recent]);
        (await DeadLetteredDeliveryCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task A_backlog_of_notifications_drains_over_several_runs_with_their_deliveries()
    {
        for (var index = 0; index < 5; index++)
        {
            await SeedNotificationAsync(Now - OneEightyDays - OneDay, DeliveryStatus.Sent, DeliveryStatus.Sent);
        }

        (await RunAsync(NotificationsCleanupJob.JobName, batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(2);
        (await NotificationIdsAsync()).Count.ShouldBe(3);
        (await DeliveryOwnersAsync()).Count.ShouldBe(6);
        (await RunAsync(NotificationsCleanupJob.JobName, batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(2);
        (await RunAsync(NotificationsCleanupJob.JobName, batchSize: 2, maxBatchesPerRun: 1)).Processed.ShouldBe(1);

        (await NotificationIdsAsync()).ShouldBeEmpty();
        (await DeliveryOwnersAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task In_app_rows_older_than_one_hundred_and_eighty_days_go_and_one_exactly_that_old_stays()
    {
        var exactly = await SeedInAppAsync(Now - OneEightyDays, read: false);
        var justOver = await SeedInAppAsync(Now - OneEightyDays - OneSecond, read: false);
        var recent = await SeedInAppAsync(Now - OneDay, read: true);

        var result = await RunAsync(InAppNotificationsCleanupJob.JobName);

        result.Processed.ShouldBe(1);
        var remaining = await InAppIdsAsync();
        remaining.ShouldBe([exactly, recent], ignoreOrder: true);
        remaining.ShouldNotContain(justOver);
    }

    [Fact]
    public async Task In_app_rows_go_read_or_unread_without_their_notification()
    {
        // The notification is old but still has a pending delivery, so it stays; its inbox rows are old and go whether or not they were read.
        var notification = await SeedNotificationAsync(Now - TimeSpan.FromDays(200), DeliveryStatus.Pending);
        await SeedInAppAsync(Now - TimeSpan.FromDays(200), read: false, notification);
        await SeedInAppAsync(Now - TimeSpan.FromDays(200), read: true, notification);
        var fresh = await SeedInAppAsync(Now - OneDay, read: false, notification);

        (await RunAsync(InAppNotificationsCleanupJob.JobName)).Processed.ShouldBe(2);

        (await InAppIdsAsync()).ShouldBe([fresh]);
        (await NotificationIdsAsync()).ShouldBe([notification]);
        (await DeliveryOwnersAsync()).ShouldBe([notification]);
    }

    [Fact]
    public async Task The_notification_job_leaves_the_in_app_rows_for_the_in_app_job()
    {
        var notification = await SeedNotificationAsync(Now - TimeSpan.FromDays(200), DeliveryStatus.Sent);
        var row = await SeedInAppAsync(Now - TimeSpan.FromDays(200), read: true, notification);

        (await RunAsync(NotificationsCleanupJob.JobName)).Processed.ShouldBe(1);
        (await InAppIdsAsync()).ShouldBe([row]);

        (await RunAsync(InAppNotificationsCleanupJob.JobName)).Processed.ShouldBe(1);
        (await InAppIdsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(AllJobNamesWithOffValues))]
    public async Task Retention_zero_or_null_disables_every_notification_job(string jobName, bool zero)
    {
        var longAgo = Now - TimeSpan.FromDays(4000);
        await SeedTicketAsync(longAgo);
        var notification = await SeedNotificationAsync(longAgo, DeliveryStatus.Sent);
        await SeedInAppAsync(longAgo, read: true, notification);
        TimeSpan? off = zero ? TimeSpan.Zero : null;
        var retention = new NotificationsRetentionOptions { HubTickets = off, Notifications = off, InAppNotifications = off };

        var result = await RunAsync(jobName, retention);

        result.Processed.ShouldBe(0);
        (await TicketKeysAsync()).Count.ShouldBe(1);
        (await NotificationIdsAsync()).Count.ShouldBe(1);
        (await DeliveryOwnersAsync()).Count.ShouldBe(1);
        (await InAppIdsAsync()).Count.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(AllJobNames))]
    public async Task A_job_constructor_opens_no_connection(string jobName)
    {
        // The scheduler resolves every job once at host start to read its name and interval.
        await using var scope = Factory.Services.CreateAsyncScope();

        var job = CreateJob(jobName, scope, new NotificationsRetentionOptions(), batchSize: 1000, maxBatchesPerRun: 100);
        _ = job.Name;
        _ = job.DefaultInterval;

        scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.GetDbConnection().State.ShouldBe(ConnectionState.Closed);
    }

    [Theory]
    [MemberData(nameof(AllJobNames))]
    public async Task Each_job_reads_its_own_retention_key(string jobName)
    {
        // Every table holds one row at each age of a grid whose steps lie between any two retentions used below, so a job that read the
        // wrong key leaves a different set of rows than the one its own key leaves. Defaults are 1 h / 180 d / 180 d.
        var tickets = new Dictionary<TimeSpan, string>();
        var notifications = new Dictionary<TimeSpan, Guid>();
        var inApp = new Dictionary<TimeSpan, Guid>();
        foreach (var age in AgeGrid)
        {
            tickets[age] = await SeedTicketAsync(Now - age);
            notifications[age] = await SeedNotificationAsync(Now - age, DeliveryStatus.Sent);
            inApp[age] = await SeedInAppAsync(Now - age, read: false);
        }

        var retention = new NotificationsRetentionOptions();
        switch (jobName)
        {
            case HubTicketsCleanupJob.JobName:
                retention.HubTickets = TimeSpan.FromDays(30);
                break;
            case NotificationsCleanupJob.JobName:
                retention.Notifications = TimeSpan.FromDays(7);
                break;
            default:
                retention.InAppNotifications = TimeSpan.FromDays(365);
                break;
        }

        foreach (var name in JobNames)
        {
            await RunAsync(name, retention);
        }

        (await TicketKeysAsync()).ShouldBe(Survivors(tickets, retention.HubTickets), ignoreOrder: true);
        (await NotificationIdsAsync()).ShouldBe(Survivors(notifications, retention.Notifications), ignoreOrder: true);
        (await InAppIdsAsync()).ShouldBe(Survivors(inApp, retention.InAppNotifications), ignoreOrder: true);
    }

    [Fact]
    public async Task Cleanup_predicates_use_their_indexes()
    {
        // Tables of mostly live rows with an old tail of about a tenth of a per cent, as real ones are: the cutoff selects few rows, so a seek on
        // the index is the plan. Set-based, so the tables are big enough that scanning them costs more than the seeks and key lookups.
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO [notify].[Notifications] ([Id], [TypeCode], [RecipientUserId], [Priority], [Culture], [Data], [SourceMessageId], [CreatedAt])
                SELECT NEWID(), 'auth.plan', {0}, 1, 'en', 'x', NEWID(),
                    CASE WHEN n % 1000 = 0 THEN DATEADD(day, -400, {1}) ELSE DATEADD(day, -(n % 100), {1}) END
                FROM (SELECT TOP (200000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS numbers;

                INSERT INTO [notify].[Deliveries] ([Id], [NotificationId], [Channel], [Status], [AttemptCount], [CreatedAt])
                SELECT NEWID(), [Id], 1, 1, 1, [CreatedAt] FROM [notify].[Notifications];

                INSERT INTO [notify].[InAppNotifications] ([Id], [UserId], [NotificationId], [TypeCode], [Category], [Title], [Body], [CreatedAt])
                SELECT NEWID(), {0}, [Id], 'auth.plan', 1, 'Title', 'Body', [CreatedAt] FROM [notify].[Notifications];

                INSERT INTO [notify].[HubTickets] ([TokenHash], [UserId], [CreatedAt], [ExpiresAt])
                SELECT CRYPT_GEN_RANDOM(32), {0}, {1},
                    CASE WHEN ROW_NUMBER() OVER (ORDER BY [Id]) % 1000 = 0 THEN DATEADD(hour, -5, {1}) ELSE DATEADD(second, 30, {1}) END
                FROM [notify].[Notifications];
                """,
                [_userId, Now.UtcDateTime],
                Ct);
        }

        await using var planScope = Factory.Services.CreateAsyncScope();
        var connection = planScope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.GetDbConnection();

        var ticketPlan = await PlanOfAsync(HubTicketsCleanupJob.JobName, planScope, connection, OneHour);
        var notificationPlan = await PlanOfAsync(NotificationsCleanupJob.JobName, planScope, connection, OneEightyDays);
        var inAppPlan = await PlanOfAsync(InAppNotificationsCleanupJob.JobName, planScope, connection, OneEightyDays);

        ticketPlan.ShouldContain("IX_HubTickets_ExpiresAt");
        SqlExecutionPlan.ClusteredIndexScansOf(ticketPlan, "[HubTickets]").ShouldBeEmpty("the cleanup must not scan the hub ticket table");
        notificationPlan.ShouldContain("IX_Notifications_CreatedAt");
        notificationPlan.ShouldContain("IX_Deliveries_NotificationId");
        SqlExecutionPlan.ClusteredIndexScansOf(notificationPlan, "[Notifications]").ShouldBeEmpty("the cleanup must not scan the notification table");
        SqlExecutionPlan.ClusteredIndexScansOf(notificationPlan, "[Deliveries]").ShouldBeEmpty("the cleanup must not scan the delivery table");
        inAppPlan.ShouldContain("IX_InAppNotifications_CreatedAt");
        SqlExecutionPlan.ClusteredIndexScansOf(inAppPlan, "[InAppNotifications]").ShouldBeEmpty("the cleanup must not scan the in-app table");
    }

    private static List<T> Survivors<T>(Dictionary<TimeSpan, T> seeded, TimeSpan? retention)
        => [.. seeded.Where(row => retention is not { } period || period == TimeSpan.Zero || row.Key <= period).Select(row => row.Value)];

    private static byte[] NewHash() => RandomNumberGenerator.GetBytes(HubTicket.TokenHashLength);

    /// <summary>
    /// The plan of the job's own statement, compiled but not run, for the cutoff the job would bind at <paramref name="retention"/>: the
    /// hint gives the compile the values the job binds as parameters, so the plan is the one the job gets.
    /// </summary>
    private async Task<string> PlanOfAsync(string jobName, AsyncServiceScope scope, System.Data.Common.DbConnection connection, TimeSpan retention)
    {
        var statement = CreateJob(jobName, scope, new NotificationsRetentionOptions(), batchSize: 1000, maxBatchesPerRun: 100).DeleteStatement;

        return await SqlExecutionPlan.OfDeleteAsync(connection, statement, (Now - retention).UtcDateTime, Ct);
    }

    private BatchedCleanupJob CreateJob(string name, AsyncServiceScope scope, NotificationsRetentionOptions retention, int batchSize, int maxBatchesPerRun)
    {
        var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var jobs = Options.Create(new JobsOptions { MaxBatchesPerRun = maxBatchesPerRun });
        var batching = Options.Create(new RetentionOptions { BatchSize = batchSize });
        var notifications = Options.Create(retention);

        return name switch
        {
            HubTicketsCleanupJob.JobName => new HubTicketsCleanupJob(context, Factory.Time, jobs, batching, notifications),
            NotificationsCleanupJob.JobName => new NotificationsCleanupJob(context, Factory.Time, jobs, batching, notifications),
            InAppNotificationsCleanupJob.JobName => new InAppNotificationsCleanupJob(context, Factory.Time, jobs, batching, notifications),
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not a Notifications cleanup job."),
        };
    }

    /// <summary>Runs the named job once with the default retentions.</summary>
    private Task<JobResult> RunAsync(string jobName, int batchSize = 1000, int maxBatchesPerRun = 100)
        => RunAsync(jobName, new NotificationsRetentionOptions(), batchSize, maxBatchesPerRun);

    private async Task<JobResult> RunAsync(string jobName, NotificationsRetentionOptions retention, int batchSize = 1000, int maxBatchesPerRun = 100)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await CreateJob(jobName, scope, retention, batchSize, maxBatchesPerRun).RunAsync(Ct);
    }

    /// <summary>
    /// A ticket that expires at exactly <paramref name="expiresAt"/> (issued thirty seconds before, as the module issues them). A consumed
    /// ticket was consumed ten seconds before it expires. Returns its key.
    /// </summary>
    private async Task<string> SeedTicketAsync(DateTimeOffset expiresAt, bool consumed = false)
    {
        var ticket = HubTicket.Issue(_userId, NewHash(), TicketLifetime, expiresAt - TicketLifetime);
        if (consumed)
        {
            ticket.TryConsume(expiresAt - TimeSpan.FromSeconds(10)).ShouldBeTrue();
        }

        await SaveAsync(ticket);
        return Convert.ToHexString(ticket.TokenHash);
    }

    /// <summary>
    /// A notification created at <paramref name="createdAt"/> with one delivery per entry of <paramref name="deliveries"/> (the first by email,
    /// the second in-app) settled as given. Returns the notification's id.
    /// </summary>
    private async Task<Guid> SeedNotificationAsync(DateTimeOffset createdAt, params DeliveryStatus[] deliveries)
    {
        var notification = Notification.Create(
            TypeCode,
            NotificationPriority.Normal,
            Guid.NewGuid(),
            "en",
            """{"displayName":"Alice"}""",
            protectedData: null,
            Guid.NewGuid(),
            correlationId: null,
            expiresAt: null,
            createdAt);
        NotificationChannel[] channels = [NotificationChannel.Email, NotificationChannel.InApp];
        for (var index = 0; index < deliveries.Length; index++)
        {
            var channel = channels[index];
            var delivery = notification.AddDelivery(channel, channel == NotificationChannel.Email ? "alice@example.com" : null, createdAt, createdAt);
            Settle(delivery, deliveries[index], createdAt);
        }

        await SaveAsync(notification);
        return notification.Id;
    }

    private static void Settle(Delivery delivery, DeliveryStatus status, DateTimeOffset now)
    {
        switch (status)
        {
            case DeliveryStatus.Sent:
                delivery.MarkSent("Subject", now);
                break;
            case DeliveryStatus.DeadLettered:
                delivery.DeadLetter("smtp.rejected", now);
                break;
            case DeliveryStatus.Expired:
                delivery.Expire();
                break;
            case DeliveryStatus.Pending:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "Not a delivery status.");
        }
    }

    private async Task<Guid> SeedInAppAsync(DateTimeOffset createdAt, bool read, Guid? notificationId = null)
    {
        var row = InAppNotification.Create(Guid.NewGuid(), _userId, notificationId ?? Guid.NewGuid(), TypeCode, NotificationCategory.Security, "Title", "Body", createdAt);
        if (read)
        {
            row.MarkRead(createdAt);
        }

        await SaveAsync(row);
        return row.Id;
    }

    private async Task SaveAsync(object entity)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        context.Add(entity);
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>What the delivery worker does with a pending delivery: settles it as sent.</summary>
    private async Task SettlePendingDeliveriesAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        foreach (var delivery in await context.Set<Delivery>().Where(candidate => candidate.Status == DeliveryStatus.Pending).ToListAsync(Ct))
        {
            delivery.MarkSent("Subject", Now);
        }

        await context.SaveChangesAsync(Ct);
    }

    private async Task<List<string>> TicketKeysAsync()
    {
        var hashes = await QueryAsync(context => context.Set<HubTicket>().Select(row => row.TokenHash).ToListAsync(Ct));
        return [.. hashes.Select(Convert.ToHexString)];
    }

    private Task<List<Guid>> NotificationIdsAsync() => QueryAsync(context => context.Set<Notification>().Select(row => row.Id).ToListAsync(Ct));

    /// <summary>The owning notification of every delivery that exists, one entry per delivery.</summary>
    private Task<List<Guid>> DeliveryOwnersAsync() => QueryAsync(context => context.Set<Delivery>().Select(row => row.NotificationId).ToListAsync(Ct));

    private Task<int> DeadLetteredDeliveryCountAsync()
        => QueryAsync(context => context.Set<Delivery>().CountAsync(row => row.Status == DeliveryStatus.DeadLettered, Ct));

    private Task<List<Guid>> InAppIdsAsync() => QueryAsync(context => context.Set<InAppNotification>().Select(row => row.Id).ToListAsync(Ct));

    private async Task<T> QueryAsync<T>(Func<NotificationsDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
    }
}
