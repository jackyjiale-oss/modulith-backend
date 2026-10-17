using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Security;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Scheduling;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Domain.Preferences;
using TemplateName.Modules.Notifications.Infrastructure.Deliveries;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;
using TemplateName.SharedKernel;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary>
/// The delivery worker against the real database, called by hand (the background service is off in tests). Deliveries are created
/// through the real scheduler with <see cref="DeliveryTestTypeSource"/>'s types, or through Auth's reset event for a real link. Email
/// goes through the real email channel to the factory's <see cref="RecordingEmailSender"/>, which records every message and fails on
/// demand; tests with several deliveries give each its own address, so the messages tell the deliveries apart.
/// </summary>
public sealed class DeliveryDispatcherTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string Email = "alice@example.com";
    private const string Token = "plaintext-token-7d2e41";
    private const string ResetLink = "http://localhost:3000/reset-password?token=" + Token;
    private const string FirstAddress = "first@example.com";
    private const string SecondAddress = "second@example.com";

    private DateTime Now => Factory.Time.GetUtcNow().UtcDateTime;

    private DeliveryDispatcher Dispatcher => Factory.Services.GetRequiredService<DeliveryDispatcher>();

    private DeliveryOptions Options => Factory.Services.GetRequiredService<IOptions<DeliveryOptions>>().Value;

    [Fact]
    public async Task Due_delivery_is_sent_once_and_marked_sent()
    {
        var user = await CreateUserAsync(Email);
        var deliveryId = await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id);

        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(0);

        var message = Factory.EmailSender.Sent.ShouldHaveSingleItem();
        message.To.ShouldBe(Email);
        message.Subject.ShouldBe("Test message for Test user");
        message.TextBody.ShouldBe("Hello Test user, this is a test message from TemplateName.");
        message.HtmlBody.ShouldNotBeNull().ShouldContain("<p>Hello Test user, this is a test message from TemplateName.</p>");

        var delivery = await DeliveryAsync(deliveryId);
        delivery.Status.ShouldBe(DeliveryStatus.Sent);
        delivery.SentAt.ShouldBe(Now);
        delivery.RenderedSubject.ShouldBe("Test message for Test user");
        delivery.AttemptCount.ShouldBe(1);
        delivery.LastError.ShouldBeNull();
        delivery.NextAttemptAt.ShouldBeNull();
        delivery.LockedUntil.ShouldBeNull();
    }

    [Fact]
    public async Task Deferred_delivery_waits_until_next_attempt_at()
    {
        // Quiet hours 22:00 to 07:00 in UTC (the test user's time zone is empty); the clock starts at midnight.
        var user = await CreateUserAsync(Email);
        await SetQuietHoursAsync(user.Id, new TimeOnly(22, 0), new TimeOnly(7, 0));
        var deliveryId = await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id);
        var dueAt = Now.AddHours(7);
        (await DeliveryAsync(deliveryId)).NextAttemptAt.ShouldBe(dueAt);

        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(0);
        Factory.Time.Advance(dueAt - Now - TimeSpan.FromSeconds(1));
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(0);
        Factory.EmailSender.Attempts.ShouldBeEmpty();

        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);

        Factory.EmailSender.Sent.ShouldHaveSingleItem().To.ShouldBe(Email);
        var delivery = await DeliveryAsync(deliveryId);
        delivery.Status.ShouldBe(DeliveryStatus.Sent);
        delivery.SentAt.ShouldBe(dueAt);
    }

    [Fact]
    public async Task Transient_failures_follow_the_schedule_and_dead_letter_after_five()
    {
        var user = await CreateUserAsync(Email);
        var deliveryId = await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id);
        Factory.EmailSender.FailAlways(new EmailSendException(isPermanent: false, "smtp_451"));
        TimeSpan[] waits = [TimeSpan.Zero, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1)];

        for (var attempt = 1; attempt <= waits.Length; attempt++)
        {
            var wait = waits[attempt - 1];
            if (wait > TimeSpan.Zero)
            {
                // Not a second early.
                Factory.Time.Advance(wait - TimeSpan.FromSeconds(1));
                (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(0);
                Factory.Time.Advance(TimeSpan.FromSeconds(1));
            }

            (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);
            Factory.EmailSender.AttemptsTo(Email).ShouldBe(attempt);

            var delivery = await DeliveryAsync(deliveryId);
            delivery.AttemptCount.ShouldBe(attempt);
            delivery.LastError.ShouldBe("smtp_451");
            delivery.LockedUntil.ShouldBeNull();
            if (attempt < waits.Length)
            {
                delivery.Status.ShouldBe(DeliveryStatus.Pending);
                delivery.NextAttemptAt.ShouldBe(Now + waits[attempt]);
                delivery.DeadLetteredAt.ShouldBeNull();
            }
            else
            {
                delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
                delivery.DeadLetteredAt.ShouldBe(Now);
                delivery.NextAttemptAt.ShouldBeNull();
            }
        }

        // The sixth delay (6 hours) is never used: a dead-lettered delivery is not picked up again.
        Factory.Time.Advance(TimeSpan.FromHours(6));
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(0);
        Factory.EmailSender.AttemptsTo(Email).ShouldBe(5);
    }

    [Fact]
    public async Task Permanent_failure_dead_letters_at_once()
    {
        var user = await CreateUserAsync(Email);
        var deliveryId = await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id);
        Factory.EmailSender.FailNext(new EmailSendException(isPermanent: true, "smtp_550"));

        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);

        var delivery = await DeliveryAsync(deliveryId);
        delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
        delivery.LastError.ShouldBe("smtp_550");
        delivery.DeadLetteredAt.ShouldBe(Now);
        delivery.AttemptCount.ShouldBe(1);
        delivery.NextAttemptAt.ShouldBeNull();
        delivery.SentAt.ShouldBeNull();

        Factory.Time.Advance(TimeSpan.FromHours(7));
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(0);
        Factory.EmailSender.AttemptsTo(Email).ShouldBe(1);
    }

    [Fact]
    public async Task Rejected_recipient_is_dead_lettered_without_retry_and_logs_no_address()
    {
        // Review Focus 3: the server rejects the address for good (550). A host whose only logger records every category at every level.
        const string Rejected = "Rejected.Person@Example.org";
        var logs = new RecordingLoggerProvider();
        await using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(_ => new LoggerFactory([logs]));
        }));
        var dispatcher = host.Services.GetRequiredService<DeliveryDispatcher>();
        var user = await CreateUserAsync(Rejected);
        await host.Services.GetRequiredService<IIntegrationEventPublisher>().PublishAsync(
            new PasswordResetRequestedIntegrationEvent(
                Guid.NewGuid(), Factory.Time.GetUtcNow(), user.Id, Rejected, Protect(ResetLink), Factory.Time.GetUtcNow().AddMinutes(30), PasswordResetReason.SelfService),
            Ct);
        Factory.EmailSender.FailNext(new EmailSendException(isPermanent: true, "smtp_550"));

        (await dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);

        var delivery = (await DeliveriesAsync()).ShouldHaveSingleItem();
        delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
        delivery.LastError.ShouldBe("smtp_550");
        delivery.AttemptCount.ShouldBe(1);
        delivery.DeadLetteredAt.ShouldBe(Now);
        delivery.NextAttemptAt.ShouldBeNull();
        delivery.LockedUntil.ShouldBeNull();

        // No retry, however long the worker keeps polling.
        foreach (var wait in new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromHours(1), TimeSpan.FromHours(6) })
        {
            Factory.Time.Advance(wait);
            (await dispatcher.ProcessBatchAsync(Ct)).ShouldBe(0);
        }

        Factory.EmailSender.Attempts.ShouldHaveSingleItem().To.ShouldBe(Rejected);
        Factory.EmailSender.Sent.ShouldBeEmpty();

        // The dead-letter line reached the recorder; neither it nor any other line, nor LastError, holds the address or the link.
        var entries = logs.Entries;
        entries.ShouldContain(entry => entry.Contains(delivery.Id.ToString(), StringComparison.Ordinal) && entry.Contains("smtp_550", StringComparison.Ordinal));
        entries.ShouldAllBe(entry => !entry.Contains(Rejected, StringComparison.OrdinalIgnoreCase)
            && !entry.Contains("Example.org", StringComparison.OrdinalIgnoreCase)
            && !entry.Contains(Token, StringComparison.Ordinal));
        delivery.LastError.ShouldNotBeNull().ShouldNotContain("Example.org", Case.Insensitive);
    }

    [Fact]
    public async Task Permanent_failure_of_one_recipient_does_not_block_the_others()
    {
        // Three deliveries in one batch; the first one the worker sends is rejected for good, the other two go out in the same batch.
        var user = await CreateUserAsync(Email);
        string[] addresses = ["one@example.com", "two@example.com", "three@example.com"];
        foreach (var address in addresses)
        {
            await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id, address);
        }

        Factory.EmailSender.FailNext(new EmailSendException(isPermanent: true, "smtp_550"));

        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(3);

        var rejected = Factory.EmailSender.Attempts[0].To;
        Factory.EmailSender.Attempts.Select(message => message.To).ShouldBe(addresses, ignoreOrder: true);
        Factory.EmailSender.Sent.Select(message => message.To).ShouldBe(addresses.Where(address => address != rejected), ignoreOrder: true);
        var deliveries = await DeliveriesAsync();
        deliveries.Single(delivery => delivery.Destination == rejected).ShouldSatisfyAllConditions(
            delivery => delivery.Status.ShouldBe(DeliveryStatus.DeadLettered),
            delivery => delivery.LastError.ShouldBe("smtp_550"));
        deliveries.Where(delivery => delivery.Destination != rejected).ShouldAllBe(delivery => delivery.Status == DeliveryStatus.Sent);
    }

    [Fact]
    public async Task Expired_link_is_not_sent()
    {
        // A reset link valid for 30 minutes whose email keeps failing: attempts at +0, +1, +6 and +21 minutes; the next one is due at
        // +81 minutes, past the link's expiry, so the delivery is expired there instead of sent with a dead link.
        var user = await CreateUserAsync(Email);
        await PublishAsync(new PasswordResetRequestedIntegrationEvent(
            Guid.NewGuid(), Factory.Time.GetUtcNow(), user.Id, Email, Protect(ResetLink), Factory.Time.GetUtcNow().AddMinutes(30), PasswordResetReason.SelfService));
        var deliveryId = (await DeliveriesAsync()).ShouldHaveSingleItem().Id;
        var expiresAt = Now.AddMinutes(30);
        Factory.EmailSender.FailAlways(new EmailSendException(isPermanent: false, "timeout"));

        await DispatchNotificationsAsync();
        foreach (var wait in new[] { 1, 5, 15 })
        {
            Factory.Time.Advance(TimeSpan.FromMinutes(wait));
            await DispatchNotificationsAsync();
        }

        Factory.EmailSender.AttemptsTo(Email).ShouldBe(4);
        (await DeliveryAsync(deliveryId)).NextAttemptAt.ShouldBe(Now.AddHours(1));
        Factory.EmailSender.Clear();

        Factory.Time.Advance(TimeSpan.FromHours(1));
        await DispatchNotificationsAsync();

        Factory.EmailSender.Attempts.ShouldBeEmpty();
        var delivery = await DeliveryAsync(deliveryId);
        delivery.ExpiresAt.ShouldBe(expiresAt);
        delivery.Status.ShouldBe(DeliveryStatus.Expired);
        delivery.SentAt.ShouldBeNull();
        delivery.NextAttemptAt.ShouldBeNull();
        delivery.LockedUntil.ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_dispatchers_send_each_delivery_once()
    {
        const int Count = 40;
        var user = await CreateUserAsync(Email);
        var addresses = Enumerable.Range(1, Count).Select(i => $"user{i}@example.com").ToList();
        foreach (var address in addresses)
        {
            await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id, address);
        }

        var first = ActivatorUtilities.CreateInstance<DeliveryDispatcher>(Factory.Services);
        var second = ActivatorUtilities.CreateInstance<DeliveryDispatcher>(Factory.Services);

        var claimed = await Task.WhenAll(Task.Run(() => DrainAsync(first), Ct), Task.Run(() => DrainAsync(second), Ct));

        claimed.Sum().ShouldBe(Count);
        var sent = Factory.EmailSender.Attempts.Select(message => message.To).ToList();
        sent.Count.ShouldBe(Count);
        sent.Distinct().Count().ShouldBe(Count);
        sent.ShouldBe(addresses, ignoreOrder: true);
        (await DeliveriesAsync()).ShouldAllBe(delivery => delivery.Status == DeliveryStatus.Sent && delivery.AttemptCount == 1);
    }

    [Fact]
    public async Task Lease_lost_update_does_not_overwrite_the_new_owner()
    {
        var user = await CreateUserAsync(Email);
        await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id, FirstAddress);
        await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id, SecondAddress);
        var loser = ActivatorUtilities.CreateInstance<DeliveryDispatcher>(Factory.Services);
        var winner = ActivatorUtilities.CreateInstance<DeliveryDispatcher>(Factory.Services);

        // The loser claims both deliveries and is parked inside the first send.
        var entered = Factory.EmailSender.Hold();
        var losing = Task.Run(() => loser.ProcessBatchAsync(Ct), Ct);
        var heldAddress = await entered.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // Its lease runs out, so the winner re-claims both deliveries and sends them.
        Factory.Time.Advance(Options.LeaseDuration + TimeSpan.FromSeconds(1));
        var winnerNow = Now;
        (await winner.ProcessBatchAsync(Ct)).ShouldBe(2);

        // The loser's send then succeeds too (at least once), but its update must not replace the winner's.
        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        Factory.EmailSender.Release();
        (await losing.WaitAsync(TimeSpan.FromSeconds(30), Ct)).ShouldBe(2);

        var deliveries = await DeliveriesAsync();
        deliveries.Count.ShouldBe(2);
        deliveries.ShouldAllBe(delivery => delivery.Status == DeliveryStatus.Sent
            && delivery.SentAt == winnerNow
            && delivery.AttemptCount == 2
            && delivery.LastError == null
            && delivery.LockedUntil == null);

        // The loser saw that its lease had run out and did not start the second delivery.
        var otherAddress = heldAddress == FirstAddress ? SecondAddress : FirstAddress;
        Factory.EmailSender.AttemptsTo(heldAddress).ShouldBe(2);
        Factory.EmailSender.AttemptsTo(otherAddress).ShouldBe(1);
    }

    [Fact]
    public async Task Missing_template_dead_letters_with_render_failed()
    {
        var user = await CreateUserAsync(Email);
        var deliveryId = await ScheduleAsync(DeliveryTestTypeSource.MissingTemplate, user.Id);

        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);

        Factory.EmailSender.Attempts.ShouldBeEmpty();
        var delivery = await DeliveryAsync(deliveryId);
        delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
        delivery.LastError.ShouldBe("render_failed");
        delivery.AttemptCount.ShouldBe(1);
    }

    [Fact]
    public async Task Plaintext_link_reaches_the_channel_but_no_row_or_log()
    {
        // A host whose only logger records everything, at every level, including EF Core's and Data Protection's own lines.
        var logs = new RecordingLoggerProvider();
        await using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(_ => new LoggerFactory([logs]));
        }));
        var user = await CreateUserAsync(Email);
        var protectedLink = host.Services.GetRequiredService<ISecretProtector>().Protect(ResetLink);
        await host.Services.GetRequiredService<IIntegrationEventPublisher>().PublishAsync(
            new PasswordResetRequestedIntegrationEvent(
                Guid.NewGuid(), Factory.Time.GetUtcNow(), user.Id, Email, protectedLink, Factory.Time.GetUtcNow().AddMinutes(30), PasswordResetReason.SelfService),
            Ct);

        (await host.Services.GetRequiredService<DeliveryDispatcher>().ProcessBatchAsync(Ct)).ShouldBe(1);

        var message = Factory.EmailSender.Sent.ShouldHaveSingleItem();
        message.TextBody.ShouldContain(ResetLink);
        message.HtmlBody.ShouldNotBeNull().ShouldContain(ResetLink);
        message.Subject.ShouldNotContain(Token);
        var delivery = (await DeliveriesAsync()).ShouldHaveSingleItem();
        delivery.Status.ShouldBe(DeliveryStatus.Sent);

        var stored = await ReadEveryValueAsync("Notifications", "Deliveries", "InAppNotifications", "InboxMessages");
        stored.ShouldNotBeEmpty();
        stored.ShouldAllBe(value => !value.Contains(Token, StringComparison.Ordinal));

        // The dispatcher's lines went through the recorder (they name the delivery), and no line anywhere carries the token.
        var entries = logs.Entries;
        entries.ShouldContain(entry => entry.Contains(delivery.Id.ToString(), StringComparison.Ordinal));
        entries.ShouldAllBe(entry => !entry.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delivery_without_a_registered_channel_dead_letters_with_channel_unavailable()
    {
        await using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.RemoveAll<INotificationChannel>()));
        var user = await CreateUserAsync(Email);
        var deliveryId = await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id);

        (await host.Services.GetRequiredService<DeliveryDispatcher>().ProcessBatchAsync(Ct)).ShouldBe(1);

        var delivery = await DeliveryAsync(deliveryId);
        delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
        delivery.LastError.ShouldBe("channel_unavailable");
        Factory.EmailSender.Attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Delivery_claimed_past_max_attempts_is_dead_lettered_without_sending()
    {
        // Attempts are counted when a delivery is claimed, so one whose earlier attempts all ended without an outcome (the process died
        // while sending, and the lease ran out each time) is given up instead of being claimed for ever.
        var user = await CreateUserAsync(Email);
        var deliveryId = await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id);
        await using (var scope = Factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<Delivery>()
                .Where(delivery => delivery.Id == deliveryId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(delivery => delivery.AttemptCount, Options.MaxAttempts), Ct);
        }

        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);

        Factory.EmailSender.Attempts.ShouldBeEmpty();
        var delivery = await DeliveryAsync(deliveryId);
        delivery.Status.ShouldBe(DeliveryStatus.DeadLettered);
        delivery.LastError.ShouldBe("attempts_exhausted");
        delivery.AttemptCount.ShouldBe(Options.MaxAttempts + 1);
    }

    [Fact]
    public async Task Shutdown_after_the_channel_answered_still_saves_the_outcome()
    {
        var user = await CreateUserAsync(Email);
        var firstId = await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id, FirstAddress);
        var secondId = await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id, SecondAddress);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        // The host starts stopping while the server is accepting the first message.
        var entered = Factory.EmailSender.Hold();
        var dispatching = Task.Run(() => Dispatcher.ProcessBatchAsync(stopping.Token), Ct);
        var sentAddress = await entered.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await stopping.CancelAsync();
        Factory.EmailSender.Release();

        // The worker stops before the next delivery, but the answered one is recorded: not left leased to be sent again.
        await Should.ThrowAsync<OperationCanceledException>(() => dispatching.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        var (sentId, notStartedId, notStartedAddress) = sentAddress == FirstAddress ? (firstId, secondId, SecondAddress) : (secondId, firstId, FirstAddress);
        var sent = await DeliveryAsync(sentId);
        sent.Status.ShouldBe(DeliveryStatus.Sent);
        sent.SentAt.ShouldBe(Now);
        sent.AttemptCount.ShouldBe(1);
        sent.LockedUntil.ShouldBeNull();

        // Later the other delivery is sent; the first is never sent again (the release of the other: see the next test).
        Factory.Time.Advance(Options.LeaseDuration + TimeSpan.FromSeconds(1));
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);
        Factory.EmailSender.AttemptsTo(sentAddress).ShouldBe(1);
        Factory.EmailSender.AttemptsTo(notStartedAddress).ShouldBe(1);
        (await DeliveryAsync(notStartedId)).Status.ShouldBe(DeliveryStatus.Sent);
    }

    [Fact]
    public async Task Unprocessed_claims_are_released_when_the_batch_is_cancelled()
    {
        // Both deliveries are claimed in one batch; the host stops while the first is being sent.
        var user = await CreateUserAsync(Email);
        var firstId = await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id, FirstAddress);
        var secondId = await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id, SecondAddress);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var entered = Factory.EmailSender.Hold();
        var dispatching = Task.Run(() => Dispatcher.ProcessBatchAsync(stopping.Token), Ct);
        var heldAddress = await entered.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await stopping.CancelAsync();
        Factory.EmailSender.Release();
        await Should.ThrowAsync<OperationCanceledException>(() => dispatching.WaitAsync(TimeSpan.FromSeconds(30), Ct));

        // The started delivery keeps the existing rules (its answered outcome is saved); the unstarted one gets its claim back.
        var (heldId, otherId, otherAddress) = heldAddress == FirstAddress ? (firstId, secondId, SecondAddress) : (secondId, firstId, FirstAddress);
        await ShouldBeSentOnceAsync(heldId, heldAddress);
        await ShouldBeReleasedAsync(otherId, otherAddress);

        // Released, it is due at once: the next batch sends it, once, without waiting for the lease.
        await ShouldBeSentByTheNextBatchAsync(otherId, otherAddress);
        Factory.EmailSender.AttemptsTo(heldAddress).ShouldBe(1);
    }

    [Fact]
    public async Task Unprocessed_claims_are_released_when_the_lease_runs_out()
    {
        var user = await CreateUserAsync(Email);
        var firstId = await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id, FirstAddress);
        var secondId = await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id, SecondAddress);

        // The first send outlasts the lease; nobody else re-claims, so its outcome is saved, but the second is not started.
        var entered = Factory.EmailSender.Hold();
        var dispatching = Task.Run(() => Dispatcher.ProcessBatchAsync(Ct), Ct);
        var heldAddress = await entered.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Factory.Time.Advance(Options.LeaseDuration + TimeSpan.FromSeconds(1));
        Factory.EmailSender.Release();
        (await dispatching.WaitAsync(TimeSpan.FromSeconds(30), Ct)).ShouldBe(2);

        var (heldId, otherId, otherAddress) = heldAddress == FirstAddress ? (firstId, secondId, SecondAddress) : (secondId, firstId, FirstAddress);
        await ShouldBeSentOnceAsync(heldId, heldAddress);
        await ShouldBeReleasedAsync(otherId, otherAddress);
        await ShouldBeSentByTheNextBatchAsync(otherId, otherAddress);
        Factory.EmailSender.AttemptsTo(heldAddress).ShouldBe(1);
    }

    [Fact]
    public async Task No_delivery_is_started_inside_the_safety_margin()
    {
        var user = await CreateUserAsync(Email);
        var firstId = await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id, FirstAddress);
        var secondId = await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id, SecondAddress);

        // The lease has not run out yet, but less than the margin is left: too little to start another send.
        var entered = Factory.EmailSender.Hold();
        var dispatching = Task.Run(() => Dispatcher.ProcessBatchAsync(Ct), Ct);
        var heldAddress = await entered.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Factory.Time.Advance(Options.LeaseDuration - Options.LeaseSafetyMargin + TimeSpan.FromSeconds(1));
        Factory.EmailSender.Release();
        (await dispatching.WaitAsync(TimeSpan.FromSeconds(30), Ct)).ShouldBe(2);

        var (heldId, otherId, otherAddress) = heldAddress == FirstAddress ? (firstId, secondId, SecondAddress) : (secondId, firstId, FirstAddress);
        await ShouldBeSentOnceAsync(heldId, heldAddress);
        await ShouldBeReleasedAsync(otherId, otherAddress);
        await ShouldBeSentByTheNextBatchAsync(otherId, otherAddress);
    }

    [Fact]
    public async Task Delivery_is_started_while_more_than_the_safety_margin_is_left()
    {
        var user = await CreateUserAsync(Email);
        await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id, FirstAddress);
        await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id, SecondAddress);

        var entered = Factory.EmailSender.Hold();
        var dispatching = Task.Run(() => Dispatcher.ProcessBatchAsync(Ct), Ct);
        await entered.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Factory.Time.Advance(Options.LeaseDuration - Options.LeaseSafetyMargin - TimeSpan.FromSeconds(1));
        Factory.EmailSender.Release();
        (await dispatching.WaitAsync(TimeSpan.FromSeconds(30), Ct)).ShouldBe(2);

        Factory.EmailSender.Sent.Select(message => message.To).ShouldBe([FirstAddress, SecondAddress], ignoreOrder: true);
        (await DeliveriesAsync()).ShouldAllBe(delivery => delivery.Status == DeliveryStatus.Sent && delivery.AttemptCount == 1);
    }

    [Fact]
    public async Task Free_text_reason_is_stored_and_logged_as_unknown()
    {
        // A channel that builds its result around the factories' check, with a server message quoting an address and a link. The real
        // email channel never does (it checks the transport's reason too), so this host replaces it.
        const string ServerMessage = "550 5.1.1 <bob@example.org> rejected, see https://mail.example.test/help";
        var logs = new RecordingLoggerProvider();
        var channel = new ScriptedEmailChannel(
            new ChannelSendResult(ChannelSendOutcome.TransientFailure, ServerMessage),
            ChannelSendResult.Permanent("smtp_550") with { Reason = ServerMessage });
        await using var host = Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILoggerFactory>();
            services.AddSingleton<ILoggerFactory>(_ => new LoggerFactory([logs]));
            services.RemoveAll<INotificationChannel>();
            services.AddSingleton<INotificationChannel>(channel);
        }));
        var dispatcher = host.Services.GetRequiredService<DeliveryDispatcher>();
        var user = await CreateUserAsync(Email);
        var deliveryId = await ScheduleAsync(DeliveryTestTypeSource.Email, user.Id);

        (await dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);
        var retried = await DeliveryAsync(deliveryId);
        retried.Status.ShouldBe(DeliveryStatus.Pending);
        retried.LastError.ShouldBe("unknown");

        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        (await dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);
        var deadLettered = await DeliveryAsync(deliveryId);
        deadLettered.Status.ShouldBe(DeliveryStatus.DeadLettered);
        deadLettered.LastError.ShouldBe("unknown");

        var entries = logs.Entries;
        entries.ShouldContain(entry => entry.Contains(deliveryId.ToString(), StringComparison.Ordinal) && entry.Contains("unknown", StringComparison.Ordinal));
        entries.ShouldAllBe(entry => !entry.Contains("bob@example.org", StringComparison.Ordinal)
            && !entry.Contains("mail.example.test", StringComparison.Ordinal)
            && !entry.Contains("5.1.1", StringComparison.Ordinal));
    }

    private static async Task<int> DrainAsync(DeliveryDispatcher dispatcher)
    {
        var total = 0;
        int claimed;
        while ((claimed = await dispatcher.ProcessBatchAsync(Ct)) > 0)
        {
            total += claimed;
        }

        return total;
    }

    private async Task ShouldBeSentOnceAsync(Guid deliveryId, string address)
    {
        var delivery = await DeliveryAsync(deliveryId);
        delivery.Status.ShouldBe(DeliveryStatus.Sent);
        delivery.AttemptCount.ShouldBe(1);
        delivery.LockedUntil.ShouldBeNull();
        Factory.EmailSender.AttemptsTo(address).ShouldBe(1);
    }

    // Claimed but never started: pending again, with no lease and the claim's attempt given back.
    private async Task ShouldBeReleasedAsync(Guid deliveryId, string address)
    {
        var delivery = await DeliveryAsync(deliveryId);
        delivery.Status.ShouldBe(DeliveryStatus.Pending);
        delivery.LockedUntil.ShouldBeNull();
        delivery.AttemptCount.ShouldBe(0);
        delivery.LastError.ShouldBeNull();
        Factory.EmailSender.AttemptsTo(address).ShouldBe(0);
    }

    // Without moving the clock: a released delivery does not wait for the lease it had.
    private async Task ShouldBeSentByTheNextBatchAsync(Guid deliveryId, string address)
    {
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(0);
        await ShouldBeSentOnceAsync(deliveryId, address);
    }

    private async Task<Guid> ScheduleAsync(string typeCode, Guid userId, string? emailOverride = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var request = new NotificationRequest(
            typeCode,
            userId,
            Guid.NewGuid(),
            nameof(DeliveryDispatcherTests),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            emailOverride,
            ExpiresAt: null);

        (await scope.ServiceProvider.GetRequiredService<NotificationScheduler>().ScheduleAsync(request, contactVariables: null, Ct))
            .ShouldBe(ScheduleOutcome.Scheduled);

        var notification = await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<Notification>()
            .Include(candidate => candidate.Deliveries)
            .AsNoTracking()
            .SingleAsync(candidate => candidate.SourceMessageId == request.SourceMessageId, Ct);

        return notification.Deliveries.ShouldHaveSingleItem().Id;
    }

    private async Task SetQuietHoursAsync(Guid userId, TimeOnly start, TimeOnly end)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        var profile = UserNotificationProfile.Create(userId, Factory.Time.GetUtcNow());
        profile.SetQuietHours(QuietHours.Create(start, end).Value, Factory.Time.GetUtcNow());
        db.Add(profile);
        await db.SaveChangesAsync(Ct);
    }

    private Task PublishAsync<TEvent>(TEvent integrationEvent)
        where TEvent : IIntegrationEvent
        => Factory.Services.GetRequiredService<IIntegrationEventPublisher>().PublishAsync(integrationEvent, Ct);

    private string Protect(string plaintext) => Factory.Services.GetRequiredService<ISecretProtector>().Protect(plaintext);

    private async Task<Delivery> DeliveryAsync(Guid id)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<Delivery>().AsNoTracking().SingleAsync(delivery => delivery.Id == id, Ct);
    }

    private async Task<List<Delivery>> DeliveriesAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<Delivery>().AsNoTracking().ToListAsync(Ct);
    }

    // Every non-null value of every column of the named notify tables, as text.
    private async Task<List<string>> ReadEveryValueAsync(params string[] tables)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var connectionString = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Database.GetConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);

        var values = new List<string>();
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM [notify].[{table}]";
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                for (var column = 0; column < reader.FieldCount; column++)
                {
                    if (!await reader.IsDBNullAsync(column, Ct))
                    {
                        values.Add(Convert.ToString(reader.GetValue(column), System.Globalization.CultureInfo.InvariantCulture)!);
                    }
                }
            }
        }

        return values;
    }

    /// <summary>An email channel that answers with the given results in turn, without sending anything.</summary>
    private sealed class ScriptedEmailChannel(params ChannelSendResult[] results) : INotificationChannel
    {
        private readonly Queue<ChannelSendResult> _results = new(results);

        public NotificationChannel Channel => NotificationChannel.Email;

        public Task<ChannelSendResult> SendAsync(DeliveryWork work, CancellationToken cancellationToken)
            => Task.FromResult(_results.Dequeue());
    }
}
