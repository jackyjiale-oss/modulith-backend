using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Infrastructure.Common.Outbox;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Verification.Events;
using TemplateName.Modules.Auth.Infrastructure.Persistence;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.InApp;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary>
/// The whole path of a notification through the running host: an Auth endpoint raises a domain event, the Auth outbox publishes the
/// integration event, the Notifications consumer writes the notification and its deliveries, and the delivery worker renders and
/// delivers them by email and in app (stored row plus a SignalR push). The database, outbox, inbox, worker, templates and hub are
/// all real; only the SMTP transport is <see cref="RecordingEmailSender"/>. The tests run the dispatchers by hand
/// (<see cref="IntegrationTestBase.DispatchNotificationsAsync"/>), so nothing here waits for a polling interval.
/// </summary>
/// <remarks>
/// SignalR closes a connection whose authentication has expired by the <b>system</b> clock, so every test starts with the host's fake
/// clock at the real time (as <c>HubTests</c> does) and does not move it: the hub principal then expires ten minutes from now.
/// </remarks>
public sealed class NotificationFlowTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string Email = "flow@example.com";
    private const string FirstPassword = "correct horse battery staple";
    private const string SecondPassword = "second horse battery staple";
    private const string InboxRoute = "/api/v1/notifications/inbox";
    private const string UnreadCountRoute = "/api/v1/notifications/inbox/unread-count";
    private const string ConfirmationSubject = "请确认您的电子邮件地址";
    private const string PasswordChangedSubject = "您的密码已更改";

    private int _requests;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Whole seconds, like the factory's start instant, so values compare equal after the database's datetime2(3) rounding.
        var now = TimeProvider.System.GetUtcNow();
        Factory.Time.SetUtcNow(new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero));
    }

    [Fact]
    public async Task Register_confirm_login_change_password_notifies_by_email_and_in_app()
    {
        // Register with a Chinese locale: the confirmation email is written in the recipient's language, not the request's.
        using (var register = await PostAsync("/api/v1/auth/register", new { email = Email, password = FirstPassword, displayName = "Flow", locale = "zh-Hans" }))
        {
            register.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        await DispatchNotificationsAsync();

        var confirmation = Factory.EmailSender.Sent.ShouldHaveSingleItem();
        confirmation.To.ShouldBe(Email);
        confirmation.Subject.ShouldBe(ConfirmationSubject);
        confirmation.TextBody.ShouldContain("请打开以下链接");
        confirmation.HtmlBody.ShouldNotBeNull().ShouldContain("请确认您的电子邮件地址");

        // Its link confirms the account.
        using (var confirm = await PostAsync("/api/v1/auth/email/confirm", new { token = Factory.EmailSender.LastLinkToken(Email) }))
        {
            confirm.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        var accessToken = await LoginAsync(FirstPassword);
        var userId = await UserIdAsync();

        // The client connects to the hub the way a browser does: a fresh single-use ticket for negotiate and for the WebSocket.
        await using var connection = NotificationHubClient.Connect(Factory.Server, async () => (string?)await IssueTicketAsync(accessToken));
        var received = NotificationHubClient.Receive(connection);
        await connection.StartAsync(Ct);
        connection.State.ShouldBe(HubConnectionState.Connected);

        // Nothing is in the inbox yet: the confirmation is an email only.
        (await UnreadCountAsync(accessToken)).ShouldBe(0);

        using (var change = await PostAsync(
            "/api/v1/auth/password/change", new { currentPassword = FirstPassword, newPassword = SecondPassword }, accessToken))
        {
            change.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        await DispatchNotificationsAsync();

        // The email, in Chinese.
        Factory.EmailSender.Sent.Count.ShouldBe(2);
        var changed = Factory.EmailSender.Sent[1];
        changed.To.ShouldBe(Email);
        changed.Subject.ShouldBe(PasswordChangedSubject);

        // The in-app row, one for this user, in Chinese too.
        var row = await QueryNotificationsAsync(context => context.Set<InAppNotification>().AsNoTracking().SingleAsync(Ct));
        row.UserId.ShouldBe(userId);
        row.TypeCode.ShouldBe("auth.password_changed");
        row.Title.ShouldBe(PasswordChangedSubject);
        row.ReadAt.ShouldBeNull();

        // The inbox lists it, unread.
        var items = await InboxAsync(accessToken);
        var item = items.ShouldHaveSingleItem();
        item.GetProperty("id").GetGuid().ShouldBe(row.Id);
        item.GetProperty("typeCode").GetString().ShouldBe("auth.password_changed");
        item.GetProperty("title").GetString().ShouldBe(row.Title);
        item.GetProperty("body").GetString().ShouldBe(row.Body);
        item.GetProperty("readAt").ValueKind.ShouldBe(JsonValueKind.Null);
        (await UnreadCountAsync(accessToken)).ShouldBe(1);

        // The hub delivered the same notification, live.
        var pushed = await NotificationHubClient.ReadAsync(received, Ct);
        pushed.GetProperty("id").GetGuid().ShouldBe(row.Id);
        pushed.GetProperty("title").GetString().ShouldBe(row.Title);
        received.TryRead(out _).ShouldBeFalse();

        // Marking it read empties the unread count.
        using (var read = await PostAsync($"{InboxRoute}/{row.Id}/read", body: null, accessToken))
        {
            read.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        (await UnreadCountAsync(accessToken)).ShouldBe(0);
        (await InboxAsync(accessToken)).ShouldHaveSingleItem().GetProperty("readAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Auth_outbox_retry_sends_one_email()
    {
        // Review focus 1, end to end: Notifications consumes the integration event, then a second consumer fails, so the Auth outbox
        // retries the publishing handler and the same event (same id) reaches Notifications again.
        using (var register = await PostAsync("/api/v1/auth/register", new { email = Email, password = FirstPassword, displayName = "Flow", locale = "en" }))
        {
            register.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        Factory.FlakySwitch.FailNext = true;

        // First round: the email goes out, but the outbox message is not done.
        await DispatchNotificationsAsync();

        Factory.EmailSender.Sent.ShouldHaveSingleItem().To.ShouldBe(Email);
        var afterFirst = await OutboxMessageAsync();
        afterFirst.ProcessedAt.ShouldBeNull();
        afterFirst.AttemptCount.ShouldBe(1);
        (await QueryNotificationsAsync(context => context.Set<Notification>().CountAsync(Ct))).ShouldBe(1);

        // Second round, after the retry delay: the outbox publishes the same event again and the inbox refuses the duplicate.
        Factory.Time.Advance(TimeSpan.FromSeconds(5));
        await DispatchNotificationsAsync();

        (await OutboxMessageAsync()).ProcessedAt.ShouldNotBeNull();
        var published = Factory.IntegrationEvents.Events;
        published.Count.ShouldBe(2);
        published.Select(integrationEvent => integrationEvent.Id).Distinct().Count().ShouldBe(1);
        Factory.EmailSender.Sent.ShouldHaveSingleItem();
        Factory.EmailSender.Attempts.ShouldHaveSingleItem();
        (await QueryNotificationsAsync(context => context.Set<Notification>().CountAsync(Ct))).ShouldBe(1);
        (await QueryNotificationsAsync(context => context.Set<Delivery>().CountAsync(Ct))).ShouldBe(1);
    }

    private async Task<string> LoginAsync(string password)
    {
        using var response = await PostAsync("/api/v1/auth/login", new { email = Email, password, deviceName = "flow" });
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("accessToken").GetString()!;
    }

    private async Task<string> IssueTicketAsync(string accessToken)
    {
        using var response = await PostAsync("/api/v1/notifications/hub-tickets", body: null, accessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return body.GetProperty("ticket").GetString()!;
    }

    private async Task<List<JsonElement>> InboxAsync(string accessToken)
    {
        using var response = await GetAsync(InboxRoute, accessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        return [.. body.GetProperty("items").EnumerateArray()];
    }

    private async Task<int> UnreadCountAsync(string accessToken)
    {
        using var response = await GetAsync(UnreadCountRoute, accessToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("isCapped").GetBoolean().ShouldBeFalse();
        return body.GetProperty("count").GetInt32();
    }

    private async Task<Guid> UserIdAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await context.Set<User>().AsNoTracking().Select(user => user.Id).SingleAsync(Ct);
    }

    private async Task<OutboxMessage> OutboxMessageAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await context.Set<OutboxMessage>().AsNoTracking()
            .SingleAsync(message => message.Type == typeof(VerificationCodeIssuedDomainEvent).FullName, Ct);
    }

    private async Task<T> QueryNotificationsAsync<T>(Func<NotificationsDbContext, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<NotificationsDbContext>());
    }

    // Every request comes from its own client address, so the strict per-address limit of the anonymous routes is never what
    // these tests measure.
    private Task<HttpResponseMessage> PostAsync(string route, object? body, string? accessToken = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, route);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return SendAsync(request, accessToken);
    }

    private Task<HttpResponseMessage> GetAsync(string route, string accessToken) => SendAsync(new HttpRequestMessage(HttpMethod.Get, route), accessToken);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string? accessToken)
    {
        using (request)
        {
            request.Headers.Add(TestClientAddressStartupFilter.HeaderName, $"10.30.0.{Interlocked.Increment(ref _requests)}");
            if (accessToken is not null)
            {
                request.WithBearer(accessToken);
            }

            return await Client.SendAsync(request, Ct);
        }
    }
}
