using NSubstitute;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.AuthEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Domain.Preferences;
using TemplateName.Modules.Notifications.Domain.PushDevices;

namespace TemplateName.UnitTests.Notifications;

/// <summary>The scheduler's push rules: one delivery per device whose session is still active, never while push is off.</summary>
public sealed class NotificationSchedulerPushTests
{
    private const string Consumer = "TemplateName.Tests.Consumer";
    private const string KualaLumpur = "Asia/Kuala_Lumpur";

    private static readonly DateTimeOffset Now = NotificationSchedulerHarness.Now;

    private readonly NotificationSchedulerHarness _harness = new();
    private readonly Guid _userId = Guid.NewGuid();

    public NotificationSchedulerPushTests()
    {
        _harness.Push.Enabled = true;
        _harness.AddContact(_userId);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task One_push_delivery_per_active_device()
    {
        var first = _harness.AddDevice(_userId);
        var second = _harness.AddDevice(_userId);

        (await ScheduleAsync(TokenReuse())).ShouldBe(ScheduleOutcome.Scheduled);

        var pushes = PushDeliveries();
        pushes.Select(delivery => delivery.Destination).ShouldBe([PushDestination.For(first.Id), PushDestination.For(second.Id)], ignoreOrder: true);
        pushes.ShouldAllBe(delivery => delivery.NextAttemptAt == Now.UtcDateTime);
    }

    [Fact]
    public async Task Push_is_dropped_while_disabled()
    {
        _harness.Push.Enabled = false;
        _harness.AddDevice(_userId);

        await ScheduleAsync(TokenReuse());

        PushDeliveries().ShouldBeEmpty();
        await _harness.PushDevices.DidNotReceiveWithAnyArgs().ListActiveAsync(default, Ct);
    }

    [Fact]
    public async Task Push_is_dropped_when_switched_off()
    {
        _harness.AddDevice(_userId);
        _harness.SetPreferences(
            _userId,
            UserPreference.Create(_userId, AuthNotificationTypes.TokenReuseDetected, NotificationChannel.Push, isEnabled: false, Now));

        await ScheduleAsync(TokenReuse());

        PushDeliveries().ShouldBeEmpty();
        _harness.Added.ShouldHaveSingleItem().Deliveries.Select(delivery => delivery.Channel)
            .ShouldBe([NotificationChannel.Email, NotificationChannel.InApp]);
    }

    [Fact]
    public async Task Devices_of_ended_sessions_are_deactivated_in_the_same_save()
    {
        var live = _harness.AddDevice(_userId);
        var ended = _harness.AddDevice(_userId, sessionActive: false);

        await ScheduleAsync(TokenReuse());

        PushDeliveries().ShouldHaveSingleItem().Destination.ShouldBe(PushDestination.For(live.Id));
        ended.IsActive.ShouldBeFalse();
        ended.DeactivationReason.ShouldBe(PushDeviceDeactivationReason.SessionEnded);
        ended.DeactivatedAt.ShouldBe(Now);
        await _harness.UnitOfWork.Received(1).SaveChangesUnlessInboxDuplicateAsync(Ct);
    }

    [Fact]
    public async Task No_device_keeps_the_other_channels()
    {
        (await ScheduleAsync(TokenReuse())).ShouldBe(ScheduleOutcome.Scheduled);

        PushDeliveries().ShouldBeEmpty();
        await _harness.Sessions.DidNotReceiveWithAnyArgs().GetActiveSessionIdsAsync(default, default!, Ct);
    }

    [Fact]
    public async Task The_excluded_session_gets_no_push()
    {
        var newDevice = _harness.AddDevice(_userId);
        var earlier = _harness.AddDevice(_userId);

        await ScheduleAsync(NewDeviceLogin() with { ExcludedSessionId = newDevice.SessionId });

        PushDeliveries().ShouldHaveSingleItem().Destination.ShouldBe(PushDestination.For(earlier.Id));
        newDevice.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task Excluding_the_only_device_keeps_the_email()
    {
        var only = _harness.AddDevice(_userId);

        (await ScheduleAsync(NewDeviceLogin() with { ExcludedSessionId = only.SessionId })).ShouldBe(ScheduleOutcome.Scheduled);

        _harness.Added.ShouldHaveSingleItem().Deliveries.ShouldHaveSingleItem().Channel.ShouldBe(NotificationChannel.Email);
        only.IsActive.ShouldBeTrue();
    }

    [Fact]
    public async Task A_non_critical_push_waits_for_quiet_hours()
    {
        _harness.AddContact(_userId, timeZone: KualaLumpur);
        _harness.SetQuietHours(_userId, new TimeOnly(22, 0), new TimeOnly(7, 0));
        _harness.AddDevice(_userId);

        await ScheduleAsync(Notice(PushTestTypeSource.PushNotice));

        // 23:30 in Kuala Lumpur: both the email and the push wait for 07:00 there (23:00 UTC).
        var deliveries = _harness.Added.ShouldHaveSingleItem().Deliveries;
        deliveries.ShouldAllBe(delivery => delivery.NextAttemptAt == new DateTime(2026, 3, 1, 23, 0, 0, DateTimeKind.Utc));
        deliveries.Select(delivery => delivery.Channel).ShouldBe([NotificationChannel.Email, NotificationChannel.Push]);
    }

    [Fact]
    public async Task A_critical_push_is_due_now()
    {
        _harness.AddContact(_userId, timeZone: KualaLumpur);
        _harness.SetQuietHours(_userId, new TimeOnly(22, 0), new TimeOnly(7, 0));
        _harness.AddDevice(_userId);

        await ScheduleAsync(TokenReuse());

        PushDeliveries().ShouldHaveSingleItem().NextAttemptAt.ShouldBe(Now.UtcDateTime);
    }

    [Fact]
    public async Task NewDeviceSignedIn_handler_excludes_the_new_session()
    {
        var newDevice = _harness.AddDevice(_userId);
        var earlier = _harness.AddDevice(_userId);
        var handler = new NewDeviceSignedInIntegrationEventHandler(_harness.CreateScheduler());

        await handler.HandleAsync(new NewDeviceSignedInIntegrationEvent(Guid.NewGuid(), Now, _userId, newDevice.SessionId, "203.0.113.7"), Ct);

        PushDeliveries().ShouldHaveSingleItem().Destination.ShouldBe(PushDestination.For(earlier.Id));
    }

    [Fact]
    public void The_request_prints_the_excluded_session()
        => (Notice(PushTestTypeSource.PushNotice) with { ExcludedSessionId = Guid.Empty }).ToString().ShouldContain("ExcludedSessionId");

    private List<Delivery> PushDeliveries()
        => [.. _harness.Added.SelectMany(notification => notification.Deliveries).Where(delivery => delivery.Channel == NotificationChannel.Push)];

    private NotificationRequest Notice(string typeCode) =>
        new(typeCode, _userId, Guid.NewGuid(), Consumer, new Dictionary<string, string>(), new Dictionary<string, string>(), EmailOverride: null, ExpiresAt: null);

    private NotificationRequest TokenReuse() =>
        Notice(AuthNotificationTypes.TokenReuseDetected) with
        {
            Variables = new Dictionary<string, string> { ["occurred_at"] = "2026-03-01 23:30", ["time_zone"] = "UTC" },
        };

    private NotificationRequest NewDeviceLogin() =>
        Notice(AuthNotificationTypes.NewDeviceLogin) with
        {
            Variables = new Dictionary<string, string> { ["occurred_at"] = "2026-03-01 23:30", ["time_zone"] = "UTC", ["ip_address"] = string.Empty },
        };

    private Task<ScheduleOutcome> ScheduleAsync(NotificationRequest request) => _harness.CreateScheduler().ScheduleAsync(request, null, Ct);
}
