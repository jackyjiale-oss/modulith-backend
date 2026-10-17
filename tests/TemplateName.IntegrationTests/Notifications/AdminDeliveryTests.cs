using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Identity;
using TemplateName.IntegrationTests.Auth;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Notifications.Application;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Scheduling;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Deliveries;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;
using TemplateName.Modules.Sample.Application;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary>
/// The delivery administration routes under <c>/api/v1/admin/notifications/deliveries</c> through the running host. Deliveries are
/// created through the real scheduler with <see cref="DeliveryTestTypeSource"/>'s types and settled by the real delivery worker (the
/// email transport fails on demand), so the list is read from rows the worker wrote, and a retry is proved by the email it ends in.
/// </summary>
public sealed class AdminDeliveryTests(IntegrationTestWebAppFactory factory) : AdminTestBase(factory)
{
    private const string ListRoute = "/api/v1/admin/notifications/deliveries";
    private const string AliceEmail = "alice@example.com";
    private const string BobEmail = "bob.smith@example.org";

    private static readonly string[] EveryPermission =
    [
        .. new IPermissionSource[] { new AuthPermissionSource(), new SamplePermissionSource(), new NotificationsPermissionSource() }
            .SelectMany(source => source.Permissions)
            .Select(permission => permission.Code),
    ];

    private DateTime Now => Factory.Time.GetUtcNow().UtcDateTime;

    private DeliveryDispatcher Dispatcher => Factory.Services.GetRequiredService<DeliveryDispatcher>();

    [Fact]
    public async Task Anonymous_is_401_and_without_permission_is_403()
    {
        var alice = await CreateUserAsync(AliceEmail);
        var deliveryId = await DeadLetteredAsync(alice.Id);
        var retryRoute = RetryRoute(deliveryId);

        // No token: 401 on both routes.
        using (var anonymous = await Client.GetAsync(ListRoute, Ct))
        {
            await AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized, "http.401");
        }

        using (var anonymousRetry = await Client.PostAsync(retryRoute, content: null, Ct))
        {
            await AssertProblemAsync(anonymousRetry, HttpStatusCode.Unauthorized, "http.401");
        }

        // Every other permission, but not the one the route needs: 403, and the retry did nothing.
        await SignInAsync([.. EveryPermission.Where(code => code != NotificationsPermissions.DeliveryView)], AuthTestHarness.DefaultLocale);
        using (var forbidden = await Client.GetAsync(ListRoute, Ct))
        {
            await AssertProblemAsync(forbidden, HttpStatusCode.Forbidden, "http.403");
        }

        await SignInAsync([.. EveryPermission.Where(code => code != NotificationsPermissions.DeliveryRetry)], AuthTestHarness.DefaultLocale);
        using (var forbiddenRetry = await Client.PostAsync(retryRoute, content: null, Ct))
        {
            await AssertProblemAsync(forbiddenRetry, HttpStatusCode.Forbidden, "http.403");
        }

        (await DeliveryAsync(deliveryId)).Status.ShouldBe(DeliveryStatus.DeadLettered);

        // The right permission, alone, is enough for its own route.
        await SignInAsync(NotificationsPermissions.DeliveryView);
        using (var allowed = await Client.GetAsync(ListRoute, Ct))
        {
            allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await SignInAsync(NotificationsPermissions.DeliveryRetry);
        using var retried = await Client.PostAsync(retryRoute, content: null, Ct);
        retried.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task List_filters_by_status_channel_type_and_recipient_and_masks_destination()
    {
        var alice = await CreateUserAsync(AliceEmail);
        var bob = await CreateUserAsync(BobEmail);

        // Four deliveries a minute apart: Alice's dead-lettered email, Bob's sent email, Alice's sent in-app notice, Alice's pending email.
        Factory.EmailSender.FailAlways(new EmailSendException(isPermanent: true, "smtp_550"));
        var deadLettered = await ScheduleAsync(DeliveryTestTypeSource.Email, alice.Id);
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);
        Factory.EmailSender.Clear();
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var sentEmail = await ScheduleAsync(DeliveryTestTypeSource.Email, bob.Id);
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var inApp = await ScheduleAsync(DeliveryTestTypeSource.InApp, alice.Id);
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        var pending = await ScheduleAsync(DeliveryTestTypeSource.Email, alice.Id);
        await SignInAsync(NotificationsPermissions.DeliveryView);

        // Newest first, every filter off.
        var all = await GetPageAsync("?includeTotalCount=true");
        all.Ids.ShouldBe([pending, inApp, sentEmail, deadLettered]);
        all.Body.GetProperty("totalCount").GetInt64().ShouldBe(4);

        // Exactly these members, camelCase, enums as names.
        var first = all.Items[0];
        first.EnumerateObject().Select(property => property.Name).ShouldBe(
            [
                "id", "notificationId", "typeCode", "recipientUserId", "channel", "destination", "status", "attemptCount", "nextAttemptAt",
                "lastError", "createdAt", "sentAt", "deadLetteredAt",
            ],
            ignoreOrder: true);
        first.GetProperty("typeCode").GetString().ShouldBe(DeliveryTestTypeSource.Email);
        first.GetProperty("recipientUserId").GetGuid().ShouldBe(alice.Id);
        first.GetProperty("channel").GetString().ShouldBe("Email");
        first.GetProperty("status").GetString().ShouldBe("Pending");
        first.GetProperty("attemptCount").GetInt32().ShouldBe(0);
        first.GetProperty("nextAttemptAt").GetDateTimeOffset().ShouldBe(new DateTimeOffset(Now, TimeSpan.Zero));
        first.GetProperty("sentAt").ValueKind.ShouldBe(JsonValueKind.Null);

        // The address is masked, never returned; an in-app delivery has none.
        var byId = all.Items.ToDictionary(item => item.GetProperty("id").GetGuid());
        byId[deadLettered].GetProperty("destination").GetString().ShouldBe("a****@example.com");
        byId[sentEmail].GetProperty("destination").GetString().ShouldBe("b****@example.org");
        byId[inApp].GetProperty("destination").ValueKind.ShouldBe(JsonValueKind.Null);
        all.Body.ToString().ShouldNotContain(AliceEmail);
        all.Body.ToString().ShouldNotContain(BobEmail);
        all.Body.ToString().ShouldNotContain("alice");
        byId[deadLettered].GetProperty("status").GetString().ShouldBe("DeadLettered");
        byId[deadLettered].GetProperty("attemptCount").GetInt32().ShouldBe(1);
        byId[deadLettered].GetProperty("lastError").GetString().ShouldBe("smtp_550");
        byId[deadLettered].GetProperty("deadLetteredAt").GetDateTimeOffset().ShouldBe(new DateTimeOffset(Now.AddMinutes(-3), TimeSpan.Zero));
        byId[sentEmail].GetProperty("status").GetString().ShouldBe("Sent");
        byId[sentEmail].GetProperty("sentAt").GetDateTimeOffset().ShouldBe(new DateTimeOffset(Now.AddMinutes(-2), TimeSpan.Zero));

        // One filter at a time, then combined.
        (await IdsAsync("?status=DeadLettered")).ShouldBe([deadLettered]);
        (await IdsAsync("?status=Pending")).ShouldBe([pending]);
        (await IdsAsync("?status=Sent")).ShouldBe([inApp, sentEmail]);
        (await IdsAsync("?status=Expired")).ShouldBeEmpty();
        (await IdsAsync("?channel=InApp")).ShouldBe([inApp]);
        (await IdsAsync("?channel=Email")).ShouldBe([pending, sentEmail, deadLettered]);
        (await IdsAsync($"?typeCode={DeliveryTestTypeSource.InApp}")).ShouldBe([inApp]);
        (await IdsAsync($"?typeCode={DeliveryTestTypeSource.Email}")).ShouldBe([pending, sentEmail, deadLettered]);
        (await IdsAsync("?typeCode=no.such_type")).ShouldBeEmpty();
        (await IdsAsync($"?recipientUserId={alice.Id}")).ShouldBe([pending, inApp, deadLettered]);
        (await IdsAsync($"?recipientUserId={bob.Id}")).ShouldBe([sentEmail]);
        (await IdsAsync($"?recipientUserId={Guid.NewGuid()}")).ShouldBeEmpty();
        (await IdsAsync($"?recipientUserId={alice.Id}&channel=Email")).ShouldBe([pending, deadLettered]);
        (await IdsAsync($"?recipientUserId={alice.Id}&channel=Email&status=DeadLettered")).ShouldBe([deadLettered]);
        (await IdsAsync($"?recipientUserId={bob.Id}&status=DeadLettered")).ShouldBeEmpty();
        (await IdsAsync($"?status=Sent&channel=Email&typeCode={DeliveryTestTypeSource.Email}&recipientUserId={bob.Id}")).ShouldBe([sentEmail]);

        // The total follows the filters.
        (await GetPageAsync($"?recipientUserId={alice.Id}&includeTotalCount=true")).Body.GetProperty("totalCount").GetInt64().ShouldBe(3);
    }

    [Fact]
    public async Task List_pages_by_cursor()
    {
        var alice = await CreateUserAsync(AliceEmail);

        // Seven deliveries on three instants (three, two, two): the id has to break every tie.
        var all = new List<Guid>();
        foreach (var onThisInstant in new[] { 3, 2, 2 })
        {
            for (var index = 0; index < onThisInstant; index++)
            {
                all.Add(await ScheduleAsync(DeliveryTestTypeSource.InApp, alice.Id));
            }

            Factory.Time.Advance(TimeSpan.FromMinutes(1));
        }

        await SignInAsync(NotificationsPermissions.DeliveryView);

        // The order SQL Server itself gives: createdAt descending, the id descending within a tie (its Guid order is not .NET's).
        var newestFirst = await QueryAsync(context => context.Set<Delivery>()
            .OrderByDescending(delivery => delivery.CreatedAt)
            .ThenByDescending(delivery => delivery.Id)
            .Select(delivery => delivery.Id)
            .ToListAsync(Ct));
        newestFirst.Count.ShouldBe(all.Count);

        var pages = await WalkForwardAsync("?pageSize=3");

        pages.Select(page => page.Ids.Count).ShouldBe([3, 3, 1]);
        pages.SelectMany(page => page.Ids).ShouldBe(newestFirst);
        pages[0].PreviousCursor.ShouldBeNull();
        pages[^1].NextCursor.ShouldBeNull();

        // Back from the last page through the previous cursors.
        var back = pages[^1];
        back.PreviousCursor.ShouldNotBeNull();
        var middle = await GetPageAsync($"?pageSize=3&cursor={Uri.EscapeDataString(back.PreviousCursor)}");
        middle.Ids.ShouldBe(pages[1].Ids);

        // Oldest first is the same walk the other way.
        var ascending = await WalkForwardAsync("?pageSize=3&sort=createdAt");
        ascending.SelectMany(page => page.Ids).ShouldBe(Enumerable.Reverse(newestFirst));

        // A cursor is tied to the filters it was made with: all four, in both directions.
        var unfiltered = await GetPageAsync("?pageSize=3");
        var cursor = Uri.EscapeDataString(unfiltered.NextCursor!);
        foreach (var filter in new[] { "status=Pending", "channel=InApp", $"typeCode={DeliveryTestTypeSource.InApp}", $"recipientUserId={alice.Id}" })
        {
            (await GetProblemAsync($"?pageSize=3&{filter}&cursor={cursor}")).GetProperty("code").GetString().ShouldBe("pagination.cursor_mismatch");
        }

        var filtered = await GetPageAsync($"?pageSize=3&status=Pending&channel=InApp&typeCode={DeliveryTestTypeSource.InApp}&recipientUserId={alice.Id}");
        filtered.Ids.ShouldBe(unfiltered.Ids);
        (await GetProblemAsync($"?pageSize=3&cursor={Uri.EscapeDataString(filtered.NextCursor!)}")).GetProperty("code").GetString().ShouldBe("pagination.cursor_mismatch");
        var rest = await GetPageAsync($"?pageSize=3&status=Pending&channel=InApp&typeCode={DeliveryTestTypeSource.InApp}&recipientUserId={alice.Id}&cursor={Uri.EscapeDataString(filtered.NextCursor!)}");
        rest.Ids.ShouldBe(pages[1].Ids);
    }

    [Fact]
    public async Task List_rejects_invalid_filters_paging_and_sort()
    {
        await SignInAsync(NotificationsPermissions.DeliveryView);

        // Values that do not parse never reach the query.
        foreach (var query in new[] { "?status=Bogus", "?channel=Fax", "?recipientUserId=not-a-guid", "?includeTotalCount=maybe" })
        {
            (await GetProblemAsync(query)).GetProperty("code").GetString().ShouldBe("request.malformed", query);
        }

        // A number the enum has no name for, and a type code over the column length.
        foreach (var query in new[] { "?status=7", "?channel=0", $"?typeCode={new string('x', 101)}" })
        {
            (await GetProblemAsync(query)).GetProperty("code").GetString().ShouldBe("validation.failed", query);
        }

        foreach (var pageSize in new[] { 0, 101 })
        {
            var body = await GetProblemAsync($"?pageSize={pageSize}");
            body.GetProperty("code").GetString().ShouldBe("validation.failed");
            body.GetProperty("errors").TryGetProperty("pageSize", out _).ShouldBeTrue();
        }

        (await GetProblemAsync("?sort=status")).GetProperty("code").GetString().ShouldBe("pagination.invalid_sort");
        (await GetProblemAsync("?sort=id")).GetProperty("code").GetString().ShouldBe("pagination.invalid_sort");
        (await GetProblemAsync("?cursor=abc")).GetProperty("code").GetString().ShouldBe("pagination.invalid_cursor");

        // An unknown type is not an error, only an empty page.
        (await GetPageAsync("?typeCode=auth.unknown_type")).Ids.ShouldBeEmpty();
    }

    [Fact]
    public async Task Retry_resends_a_dead_letter_once()
    {
        var alice = await CreateUserAsync(AliceEmail);
        var deliveryId = await ScheduleAsync(DeliveryTestTypeSource.Email, alice.Id);

        // The first attempt is refused for good, so the delivery is dead-lettered and nothing is sent.
        Factory.EmailSender.FailAlways(new EmailSendException(isPermanent: true, "smtp_550"));
        await DispatchNotificationsAsync();
        var deadLettered = await DeliveryAsync(deliveryId);
        deadLettered.Status.ShouldBe(DeliveryStatus.DeadLettered);
        deadLettered.LastError.ShouldBe("smtp_550");
        Factory.EmailSender.Sent.ShouldBeEmpty();

        // The problem is fixed, and an administrator retries.
        Factory.EmailSender.Clear();
        Factory.Time.Advance(TimeSpan.FromMinutes(5));
        await SignInAsync(NotificationsPermissions.DeliveryRetry);
        using (var response = await Client.PostAsync(RetryRoute(deliveryId), content: null, Ct))
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
        }

        // Back in the queue with a fresh budget, due now; the retry itself sent nothing.
        var queued = await DeliveryAsync(deliveryId);
        queued.Status.ShouldBe(DeliveryStatus.Pending);
        queued.AttemptCount.ShouldBe(0);
        queued.NextAttemptAt.ShouldBe(Now);
        queued.LastError.ShouldBeNull();
        queued.DeadLetteredAt.ShouldBeNull();
        queued.LockedUntil.ShouldBeNull();
        Factory.EmailSender.Attempts.ShouldBeEmpty();

        await DispatchNotificationsAsync();

        var message = Factory.EmailSender.Sent.ShouldHaveSingleItem();
        message.To.ShouldBe(AliceEmail);
        var sent = await DeliveryAsync(deliveryId);
        sent.Status.ShouldBe(DeliveryStatus.Sent);
        sent.SentAt.ShouldBe(Now);
        sent.AttemptCount.ShouldBe(1);
        sent.LastError.ShouldBeNull();

        // Nothing is due any more, so a further run sends nothing.
        await DispatchNotificationsAsync();
        Factory.EmailSender.Sent.Count.ShouldBe(1);
        Factory.EmailSender.Attempts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Retry_of_a_sent_delivery_is_409()
    {
        var alice = await CreateUserAsync(AliceEmail);
        var sentId = await ScheduleAsync(DeliveryTestTypeSource.Email, alice.Id);
        await DispatchNotificationsAsync();
        (await DeliveryAsync(sentId)).Status.ShouldBe(DeliveryStatus.Sent);
        var pendingId = await ScheduleAsync(DeliveryTestTypeSource.InApp, alice.Id);
        await SignInAsync(NotificationsPermissions.DeliveryRetry);

        // Sent and pending deliveries are not dead letters: 409, and nothing changes (so nothing is sent twice).
        foreach (var id in new[] { sentId, pendingId })
        {
            using var response = await Client.PostAsync(RetryRoute(id), content: null, Ct);
            await AssertProblemAsync(response, HttpStatusCode.Conflict, "notifications.delivery_not_retryable");
        }

        (await DeliveryAsync(sentId)).Status.ShouldBe(DeliveryStatus.Sent);
        (await DeliveryAsync(pendingId)).Status.ShouldBe(DeliveryStatus.Pending);
        await DispatchNotificationsAsync();
        Factory.EmailSender.Sent.ShouldHaveSingleItem();

        // An unknown id is a 404 that names it.
        var unknown = Guid.NewGuid();
        using var missing = await Client.PostAsync(RetryRoute(unknown), content: null, Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = await missing.Content.ReadFromJsonAsync<JsonElement>(Ct);
        problem.GetProperty("code").GetString().ShouldBe("notifications.delivery_not_found");
        problem.GetProperty("params").GetProperty("id").GetGuid().ShouldBe(unknown);
    }

    [Fact]
    public async Task Retry_of_an_expired_link_is_409()
    {
        var alice = await CreateUserAsync(AliceEmail);
        var expiresAt = Factory.Time.GetUtcNow().AddMinutes(30);

        // One link is refused for good in time; another waits past its expiry and is marked Expired by the worker.
        Factory.EmailSender.FailAlways(new EmailSendException(isPermanent: true, "smtp_550"));
        var deliveryId = await ScheduleAsync(DeliveryTestTypeSource.Email, alice.Id, expiresAt);
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);
        Factory.EmailSender.Clear();
        (await DeliveryAsync(deliveryId)).Status.ShouldBe(DeliveryStatus.DeadLettered);
        var lateId = await ScheduleAsync(DeliveryTestTypeSource.Email, alice.Id, expiresAt);
        Factory.Time.Advance(TimeSpan.FromMinutes(31));
        await Dispatcher.ProcessBatchAsync(Ct);
        (await DeliveryAsync(lateId)).Status.ShouldBe(DeliveryStatus.Expired);
        await SignInAsync(NotificationsPermissions.DeliveryRetry);

        // A dead letter past its expiry would resend something stale: 409 notifications.delivery_expired, and it stays dead.
        using (var response = await Client.PostAsync(RetryRoute(deliveryId), content: null, Ct))
        {
            await AssertProblemAsync(response, HttpStatusCode.Conflict, "notifications.delivery_expired");
        }

        (await DeliveryAsync(deliveryId)).Status.ShouldBe(DeliveryStatus.DeadLettered);

        // An expired one is simply not retryable.
        using (var late = await Client.PostAsync(RetryRoute(lateId), content: null, Ct))
        {
            await AssertProblemAsync(late, HttpStatusCode.Conflict, "notifications.delivery_not_retryable");
        }

        await DispatchNotificationsAsync();
        Factory.EmailSender.Attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Concurrent_retries_put_the_delivery_back_once()
    {
        var alice = await CreateUserAsync(AliceEmail);
        await SignInAsync(NotificationsPermissions.DeliveryRetry);

        // Several rounds, each on a fresh dead letter, because the race is a matter of timing.
        for (var round = 0; round < 6; round++)
        {
            Factory.EmailSender.Clear();
            var deliveryId = await DeadLetteredAsync(alice.Id);

            var responses = await Task.WhenAll(
                Client.PostAsync(RetryRoute(deliveryId), content: null, Ct),
                Client.PostAsync(RetryRoute(deliveryId), content: null, Ct));
            try
            {
                // Never a 500: one request retried, the other found the delivery already back in the queue.
                responses.Select(response => response.StatusCode).Order().ShouldBe([HttpStatusCode.Accepted, HttpStatusCode.Conflict]);
                await AssertProblemAsync(
                    responses.Single(response => response.StatusCode == HttpStatusCode.Conflict),
                    HttpStatusCode.Conflict,
                    "notifications.delivery_not_retryable");
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }

            // Exactly one send, however many requests there were.
            Factory.EmailSender.Clear();
            await DispatchNotificationsAsync();
            Factory.EmailSender.Sent.ShouldHaveSingleItem();
            (await DeliveryAsync(deliveryId)).Status.ShouldBe(DeliveryStatus.Sent);
        }
    }

    [Fact]
    public async Task A_stale_retry_cannot_restore_a_delivery_that_was_sent_in_the_meantime()
    {
        var alice = await CreateUserAsync(AliceEmail);
        var deliveryId = await DeadLetteredAsync(alice.Id);
        await SignInAsync(NotificationsPermissions.DeliveryRetry);

        // A second administrator read the delivery while it was still dead-lettered...
        await using var staleScope = Factory.Services.CreateAsyncScope();
        var stale = (await staleScope.ServiceProvider.GetRequiredService<INotificationRepository>().GetDeliveryAsync(deliveryId, Ct)).ShouldNotBeNull();
        stale.Status.ShouldBe(DeliveryStatus.DeadLettered);

        // ...the first one retried it, and the worker sent it.
        using (var first = await Client.PostAsync(RetryRoute(deliveryId), content: null, Ct))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await DispatchNotificationsAsync();
        Factory.EmailSender.Sent.ShouldHaveSingleItem();

        // The stale retry is refused by the database, so the delivery is not requeued and not sent a second time.
        stale.Retry(Factory.Time.GetUtcNow()).IsSuccess.ShouldBeTrue();
        await Should.ThrowAsync<DbUpdateConcurrencyException>(
            () => staleScope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct));

        (await DeliveryAsync(deliveryId)).Status.ShouldBe(DeliveryStatus.Sent);
        await DispatchNotificationsAsync();
        Factory.EmailSender.Sent.ShouldHaveSingleItem();

        // A retry started now reads the sent delivery and says so.
        using var second = await Client.PostAsync(RetryRoute(deliveryId), content: null, Ct);
        await AssertProblemAsync(second, HttpStatusCode.Conflict, "notifications.delivery_not_retryable");
    }

    private static string RetryRoute(Guid deliveryId) => $"{ListRoute}/{deliveryId}/retry";

    /// <summary>A delivery of Alice's that the worker gave up on at once (a permanently refused address), with the transport healthy again.</summary>
    private async Task<Guid> DeadLetteredAsync(Guid userId)
    {
        Factory.EmailSender.FailAlways(new EmailSendException(isPermanent: true, "smtp_550"));
        var deliveryId = await ScheduleAsync(DeliveryTestTypeSource.Email, userId);
        (await Dispatcher.ProcessBatchAsync(Ct)).ShouldBe(1);
        Factory.EmailSender.Clear();
        (await DeliveryAsync(deliveryId)).Status.ShouldBe(DeliveryStatus.DeadLettered);
        return deliveryId;
    }

    private async Task<Guid> ScheduleAsync(string typeCode, Guid userId, DateTimeOffset? expiresAt = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var request = new NotificationRequest(
            typeCode,
            userId,
            Guid.NewGuid(),
            nameof(AdminDeliveryTests),
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            EmailOverride: null,
            expiresAt);

        (await scope.ServiceProvider.GetRequiredService<NotificationScheduler>().ScheduleAsync(request, contactVariables: null, Ct))
            .ShouldBe(ScheduleOutcome.Scheduled);

        var notification = await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<Notification>()
            .Include(candidate => candidate.Deliveries)
            .AsNoTracking()
            .SingleAsync(candidate => candidate.SourceMessageId == request.SourceMessageId, Ct);

        return notification.Deliveries.ShouldHaveSingleItem().Id;
    }

    private Task<Delivery> DeliveryAsync(Guid id)
        => QueryAsync(context => context.Set<Delivery>().AsNoTracking().SingleAsync(delivery => delivery.Id == id, Ct));

    private async Task<T> QueryAsync<T>(Func<NotificationsDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
    }

    private async Task<List<Guid>> IdsAsync(string queryString) => (await GetPageAsync(queryString)).Ids;

    private async Task<Page> GetPageAsync(string queryString)
    {
        using var response = await Client.GetAsync(ListRoute + queryString, Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body.ToString());
        return new Page(body);
    }

    private async Task<JsonElement> GetProblemAsync(string queryString)
    {
        using var response = await Client.GetAsync(ListRoute + queryString, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, queryString);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }

    /// <summary>Follows <c>nextCursor</c> from the first page until it is null.</summary>
    private async Task<List<Page>> WalkForwardAsync(string queryString)
    {
        var pages = new List<Page> { await GetPageAsync(queryString) };
        while (pages[^1].NextCursor is { } nextCursor)
        {
            pages.Count.ShouldBeLessThan(50, "the walk does not end");
            pages.Add(await GetPageAsync($"{queryString}&cursor={Uri.EscapeDataString(nextCursor)}"));
        }

        return pages;
    }

    private sealed record Page(JsonElement Body)
    {
        public List<JsonElement> Items => [.. Body.GetProperty("items").EnumerateArray()];

        public List<Guid> Ids => [.. Items.Select(item => item.GetProperty("id").GetGuid())];

        public string? NextCursor => Body.GetProperty("nextCursor").GetString();

        public string? PreviousCursor => Body.GetProperty("previousCursor").GetString();
    }
}
