using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Scheduling;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.InApp;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Channels.InApp;
using TemplateName.Modules.Notifications.Infrastructure.Deliveries;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary>
/// The in-app channel through the delivery worker against the real database: the inbox row is saved with the delivery's <c>Sent</c>
/// update and keyed by the delivery id, so it exists exactly once, and the live push afterwards is best effort.
/// </summary>
public sealed class InAppChannelTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private DateTime Now => Factory.Time.GetUtcNow().UtcDateTime;

    private DeliveryDispatcher Dispatcher => Factory.Services.GetRequiredService<DeliveryDispatcher>();

    [Fact]
    public async Task In_app_delivery_creates_one_row_with_the_delivery_id()
    {
        var user = await CreateUserAsync("alice@example.com");
        var (notificationId, deliveryId) = await ScheduleInAppAsync(user.Id);

        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);

        var row = (await InAppRowsAsync()).ShouldHaveSingleItem();
        row.Id.ShouldBe(deliveryId);
        row.UserId.ShouldBe(user.Id);
        row.NotificationId.ShouldBe(notificationId);
        row.TypeCode.ShouldBe(DeliveryTestTypeSource.InApp);
        row.Category.ShouldBe(NotificationCategory.System);
        row.Title.ShouldBe("Test notice for Test user");
        row.Body.ShouldBe("Hello Test user, this is a test notice from TemplateName.");
        row.CreatedAt.ShouldBe(Now);
        row.ReadAt.ShouldBeNull();

        var delivery = await DeliveryAsync(deliveryId);
        delivery.Status.ShouldBe(DeliveryStatus.Sent);
        delivery.SentAt.ShouldBe(Now);
        delivery.RenderedSubject.ShouldBe(row.Title);

        // Pushed once the row was saved, with the row's content.
        var (userId, payload) = Factory.InAppPusher.Pushes.ShouldHaveSingleItem();
        userId.ShouldBe(user.Id);
        payload.ShouldBe(new InAppNotificationPushed(deliveryId, row.TypeCode, row.Category, row.Title, row.Body, row.CreatedAt));
        Factory.EmailSender.Attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Retry_after_crash_does_not_duplicate_the_row()
    {
        var user = await CreateUserAsync("alice@example.com");
        var (_, deliveryId) = await ScheduleInAppAsync(user.Id);
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);
        var createdAt = Now;

        // As if the row had been saved but the delivery's outcome had not: the delivery is pending again while its row exists.
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<Delivery>()
                .Where(delivery => delivery.Id == deliveryId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(delivery => delivery.Status, DeliveryStatus.Pending)
                        .SetProperty(delivery => delivery.SentAt, (DateTime?)null)
                        .SetProperty(delivery => delivery.RenderedSubject, (string?)null)
                        .SetProperty(delivery => delivery.NextAttemptAt, Now),
                    Ct);
        }

        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);

        // The duplicate key counts as delivered: still one row (the first), and the delivery is sent.
        var row = (await InAppRowsAsync()).ShouldHaveSingleItem();
        row.Id.ShouldBe(deliveryId);
        row.CreatedAt.ShouldBe(createdAt);
        var delivery = await DeliveryAsync(deliveryId);
        delivery.Status.ShouldBe(DeliveryStatus.Sent);
        delivery.SentAt.ShouldBe(Now);
        delivery.LastError.ShouldBeNull();
        delivery.AttemptCount.ShouldBe(2);
    }

    [Fact]
    public async Task Push_failure_does_not_fail_the_delivery()
    {
        var user = await CreateUserAsync("alice@example.com");
        var (_, deliveryId) = await ScheduleInAppAsync(user.Id);
        Factory.InAppPusher.FailWith(new InvalidOperationException("The hub is down."));

        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);

        (await InAppRowsAsync()).ShouldHaveSingleItem().Id.ShouldBe(deliveryId);
        var delivery = await DeliveryAsync(deliveryId);
        delivery.Status.ShouldBe(DeliveryStatus.Sent);
        delivery.LastError.ShouldBeNull();
        Factory.InAppPusher.Pushes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Push_that_never_completes_is_abandoned_after_the_timeout()
    {
        // A host whose only logger records everything, so the Warning and the absence of the rendered text can be checked.
        var logs = new RecordingLoggerProvider();
        await using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(_ => new LoggerFactory([logs]));
        }));
        var user = await CreateUserAsync("alice@example.com");
        var (_, deliveryId) = await ScheduleInAppAsync(user.Id);
        var parked = Factory.InAppPusher.HoldForever();

        var batch = host.Services.GetRequiredService<DeliveryDispatcher>().ProcessBatchAsync(Ct);
        await parked.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // The outcome is saved before the push starts.
        (await DeliveryAsync(deliveryId)).Status.ShouldBe(DeliveryStatus.Sent);

        // The bound runs on the injected clock: just short of it the worker still waits; at it, it gives up on the push and moves on.
        await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        Factory.Time.Advance(InAppChannel.PushTimeout - TimeSpan.FromMilliseconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        batch.IsCompleted.ShouldBeFalse();
        Factory.Time.Advance(TimeSpan.FromMilliseconds(1));

        (await batch.WaitAsync(TimeSpan.FromSeconds(30), Ct)).ShouldBe(1);
        var delivery = await DeliveryAsync(deliveryId);
        delivery.Status.ShouldBe(DeliveryStatus.Sent);
        delivery.LastError.ShouldBeNull();
        logs.Entries.ShouldContain(entry => entry.StartsWith("Warning", StringComparison.Ordinal)
            && entry.Contains("timed out", StringComparison.Ordinal)
            && entry.Contains(deliveryId.ToString(), StringComparison.Ordinal));
        logs.Entries.ShouldAllBe(entry => !entry.Contains("Test notice for Test user", StringComparison.Ordinal));
    }

    private async Task<(Guid NotificationId, Guid DeliveryId)> ScheduleInAppAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var request = new NotificationRequest(
            DeliveryTestTypeSource.InApp,
            userId,
            Guid.NewGuid(),
            nameof(InAppChannelTests),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            EmailOverride: null,
            ExpiresAt: null);

        (await scope.ServiceProvider.GetRequiredService<NotificationScheduler>().ScheduleAsync(request, contactVariables: null, Ct))
            .ShouldBe(ScheduleOutcome.Scheduled);

        var notification = await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<Notification>()
            .Include(candidate => candidate.Deliveries)
            .AsNoTracking()
            .SingleAsync(candidate => candidate.SourceMessageId == request.SourceMessageId, Ct);

        return (notification.Id, notification.Deliveries.ShouldHaveSingleItem().Id);
    }

    private async Task<List<InAppNotification>> InAppRowsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<InAppNotification>().AsNoTracking().ToListAsync(Ct);
    }

    private async Task<Delivery> DeliveryAsync(Guid id)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<Delivery>().AsNoTracking().SingleAsync(delivery => delivery.Id == id, Ct);
    }
}
