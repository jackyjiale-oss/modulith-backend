using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using TemplateName.Application.Common.Messaging;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.Deliveries;
using TemplateName.Modules.Notifications.Domain.InApp;
using TemplateName.Modules.Notifications.Infrastructure.Channels.InApp;
using TemplateName.Modules.Notifications.Infrastructure.Hubs;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;

namespace TemplateName.IntegrationTests.Redis;

/// <summary>
/// The SignalR backplane with <c>Caching:Provider = redis</c> (spec 3.3, D13): two hosts (two API instances) on one Valkey. The notification
/// is created and delivered on host A (its Auth outbox and delivery worker, run by hand from A's services), the client is connected to host
/// B, with tickets from B. The per-user ticket window is one Redis count for both hosts. With Valkey stopped the delivery still succeeds
/// (the push is best effort, ADR 0021); once it is back, pushes cross instances again, to a connection that stayed open and through a host
/// started meanwhile. Without Redis the hub is the in-memory one it always was. Every test uses its own
/// <c>Caching:Redis:InstanceName</c>, so neither channels nor counts leak between tests.
/// </summary>
/// <remarks>
/// SignalR closes a connection whose authentication has expired by the <b>system</b> clock, so every test starts with the factory's fake
/// clock (which the derived hosts share) at the real time, as <c>HubTests</c> does.
/// </remarks>
[Collection(RedisCollection.Name)]
public sealed class RedisBackplaneTests(IntegrationTestWebAppFactory factory, ValkeyFixture valkey) : IntegrationTestBase(factory)
{
    private const string TicketsPath = "api/v1/notifications/hub-tickets";

    // The command timeout (500 ms by default) plus room for a busy test machine.
    private static readonly TimeSpan PushCeiling = TimeSpan.FromSeconds(1);

    private readonly string _instanceName = $"backplane-{Guid.NewGuid():N}:";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await valkey.StartAsync(Ct);

        // Whole seconds, like the factory's start instant, so values compare equal after the database's datetime2(3) rounding.
        var now = TimeProvider.System.GetUtcNow();
        Factory.Time.SetUtcNow(new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero));
    }

    [Fact]
    public async Task A_push_made_on_host_A_reaches_a_client_connected_to_host_B()
    {
        await using var hostA = await CreateHostAsync();
        await using var hostB = await CreateHostAsync();
        var user = await SignInAsync();
        using var clientB = await CreateClientAsync(hostB);
        await using var connection = NotificationHubClient.Connect(hostB.Server, () => TicketFor(clientB));
        var received = NotificationHubClient.Receive(connection);
        await connection.StartAsync(Ct);
        connection.State.ShouldBe(HubConnectionState.Connected);

        // Created and delivered on A: A's outbox, A's delivery worker, A's in-app channel and A's hub context.
        await PublishPasswordChangedAsync(hostA, user.UserId);
        await DispatchNotificationsAsync(hostA.Services);

        var payload = await NotificationHubClient.ReadAsync(received, Ct);
        var row = await InAppRowAsync(user.UserId);
        payload.GetProperty("id").GetGuid().ShouldBe(row.Id);
        payload.GetProperty("typeCode").GetString().ShouldBe("auth.password_changed");
        payload.GetProperty("category").GetString().ShouldBe("Security");
        Factory.InAppPusher.Pushes.ShouldHaveSingleItem().UserId.ShouldBe(user.UserId);

        // The channel the user's connection listens on, under the instance's prefix.
        var channels = await ChannelsAsync(hostA);
        channels.ShouldContain($"{_instanceName}signalr{typeof(NotificationsHub).FullName}:user:{user.UserId}");
    }

    [Fact]
    public async Task The_ticket_limit_is_shared_across_hosts()
    {
        await using var hostA = await CreateHostAsync(("Notifications:Hub:TicketsPerMinute", "2"));
        await using var hostB = await CreateHostAsync(("Notifications:Hub:TicketsPerMinute", "2"));
        var user = await SignInAsync();
        using var clientA = await CreateClientAsync(hostA);
        using var clientB = await CreateClientAsync(hostB);

        (await PostTicketStatusAsync(clientA)).ShouldBe(HttpStatusCode.OK);
        (await PostTicketStatusAsync(clientB)).ShouldBe(HttpStatusCode.OK);

        using var refused = await clientA.PostAsync(TicketsPath, content: null, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("rate_limit.exceeded");
        refused.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        (await PostTicketStatusAsync(clientB)).ShouldBe(HttpStatusCode.TooManyRequests);

        // One count in Redis for the user, whichever host took the request; refused requests are counted too.
        var count = await Database(hostA).StringGetAsync($"{_instanceName}rl:notifications-hub-tickets:user:{user.UserId}");
        ((long)count).ShouldBe(4);
    }

    [Fact]
    public async Task Valkey_stopped_a_notification_still_delivers_and_its_inbox_row_exists()
    {
        var logs = new RecordingLoggerProvider();
        await using var host = RedisTestHosts.Create(Factory, valkey, ("Caching:Redis:InstanceName", _instanceName))
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ILoggerFactory>();
                services.AddSingleton<ILoggerFactory>(_ => new LoggerFactory([logs]));
            }));
        await RedisTestHosts.WaitUntilConnectedAsync(host, Ct);
        var user = await SignInAsync();
        using var client = await CreateClientAsync(host);
        await using var connection = NotificationHubClient.Connect(host.Server, () => TicketFor(client));
        await connection.StartAsync(Ct);
        await PublishPasswordChangedAsync(host, user.UserId);

        await valkey.StopAsync(Ct);
        try
        {
            await RedisTestHosts.WaitUntilDisconnectedAsync(host, Ct);

            await DispatchNotificationsAsync(host.Services);

            var row = await InAppRowAsync(user.UserId);
            (await DeliveryAsync(row.Id)).Status.ShouldBe(DeliveryStatus.Sent);
            Factory.InAppPusher.Pushes.ShouldHaveSingleItem().Payload.Id.ShouldBe(row.Id);

            // The host's real pusher, as the delivery worker calls it: it neither throws nor waits beyond the command timeout.
            var pusher = ActivatorUtilities.CreateInstance<SignalRInAppNotificationPusher>(host.Services);
            var payload = new InAppNotificationPushed(row.Id, row.TypeCode, row.Category, row.Title, row.Body, row.CreatedAt);
            var elapsed = Stopwatch.StartNew();
            await Should.NotThrowAsync(() => pusher.PushAsync(user.UserId, payload, Ct));
            elapsed.Elapsed.ShouldBeLessThan(PushCeiling);

            // The pusher logged the failed push itself (twice: the delivery's and the direct one); nothing reached the in-app channel.
            var pusherWarnings = logs.Entries.Where(entry => entry.StartsWith($"Warning {typeof(SignalRInAppNotificationPusher).FullName}:", StringComparison.Ordinal));
            pusherWarnings.Count().ShouldBe(2);
            logs.Entries.ShouldNotContain(entry => entry.StartsWith($"Warning {typeof(InAppChannel).FullName}:", StringComparison.Ordinal));
        }
        finally
        {
            await valkey.StartAsync(Ct);
        }
    }

    [Fact]
    public async Task After_Valkey_comes_back_pushes_cross_instances_again()
    {
        await using var hostA = await CreateHostAsync();
        await using var hostB = await CreateHostAsync();
        var user = await SignInAsync();
        using var clientB = await CreateClientAsync(hostB);
        await using var existing = NotificationHubClient.Connect(hostB.Server, () => TicketFor(clientB));
        var receivedByExisting = NotificationHubClient.Receive(existing);
        await existing.StartAsync(Ct);

        WebApplicationFactory<Program> startedWhileDown;
        await valkey.StopAsync(Ct);
        try
        {
            await RedisTestHosts.WaitUntilDisconnectedAsync(hostB, Ct);

            // A new connection cannot subscribe to its channels: the server closes it, with an error, right after the handshake.
            await using var refused = NotificationHubClient.Connect(hostB.Server, () => TicketFor(clientB));
            var closed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            refused.Closed += error =>
            {
                closed.TrySetResult(error);
                return Task.CompletedTask;
            };
            await refused.StartAsync(Ct);
            (await closed.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct)).ShouldNotBeNull();

            startedWhileDown = RedisTestHosts.Create(Factory, valkey, ("Caching:Redis:InstanceName", _instanceName));
            _ = startedWhileDown.Services;
        }
        finally
        {
            await valkey.StartAsync(Ct);
        }

        await using (startedWhileDown)
        {
            using var clientC = await CreateClientAsync(startedWhileDown);
            await using var late = NotificationHubClient.Connect(startedWhileDown.Server, () => TicketFor(clientC));
            var receivedByLate = NotificationHubClient.Receive(late);
            await RedisTestHosts.WaitUntilConnectedAsync(startedWhileDown, Ct);
            await late.StartAsync(Ct);

            // The existing connection's subscription is restored by the reconnect; the late host's connection subscribed as it opened.
            var channel = $"{_instanceName}signalr{typeof(NotificationsHub).FullName}:user:{user.UserId}";
            await RedisTestHosts.EventuallyAsync(async () => await SubscribersAsync(hostA, channel) == 2, "both connections to be subscribed", Ct);

            await PublishPasswordChangedAsync(hostA, user.UserId);
            await DispatchNotificationsAsync(hostA.Services);

            var id = (await InAppRowAsync(user.UserId)).Id;
            (await NotificationHubClient.ReadAsync(receivedByExisting, Ct)).GetProperty("id").GetGuid().ShouldBe(id);
            (await NotificationHubClient.ReadAsync(receivedByLate, Ct)).GetProperty("id").GetGuid().ShouldBe(id);
        }
    }

    [Fact]
    public async Task Without_Redis_the_hub_works_as_before()
    {
        // The factory's own host has the default provider: no Redis connection and SignalR's in-memory lifetime manager. HubTests, which
        // run on that host, are unchanged; this checks the registration and one push end to end.
        Factory.Services.GetService<IConnectionMultiplexer>().ShouldBeNull();
        Factory.Services.GetRequiredService<HubLifetimeManager<NotificationsHub>>().ShouldBeOfType<DefaultHubLifetimeManager<NotificationsHub>>();

        var user = await SignInAsync();
        await using var connection = NotificationHubClient.Connect(Factory.Server, () => TicketFor(Client));
        var received = NotificationHubClient.Receive(connection);
        await connection.StartAsync(Ct);

        await PublishPasswordChangedAsync(Factory, user.UserId);
        await DispatchNotificationsAsync();

        (await NotificationHubClient.ReadAsync(received, Ct)).GetProperty("id").GetGuid().ShouldBe((await InAppRowAsync(user.UserId)).Id);
    }

    /// <summary>A host on this test's Valkey prefix, connected before it is used, so its first request does not race the connect.</summary>
    private async Task<WebApplicationFactory<Program>> CreateHostAsync(params (string Key, string Value)[] settings)
    {
        var host = RedisTestHosts.Create(Factory, valkey, [("Caching:Redis:InstanceName", _instanceName), .. settings]);
        await RedisTestHosts.WaitUntilConnectedAsync(host, Ct);
        return host;
    }

    private static async Task PublishPasswordChangedAsync(WebApplicationFactory<Program> host, Guid userId)
    {
        var time = host.Services.GetRequiredService<TimeProvider>();
        await host.Services.GetRequiredService<IIntegrationEventPublisher>()
            .PublishAsync(new PasswordChangedIntegrationEvent(Guid.NewGuid(), time.GetUtcNow(), userId), Ct);
    }

    private static async Task<HttpStatusCode> PostTicketStatusAsync(HttpClient client)
    {
        using var response = await client.PostAsync(TicketsPath, content: null, Ct);
        return response.StatusCode;
    }

    /// <summary>An <c>AccessTokenProvider</c> call: a new ticket each time, from the host <paramref name="client"/> talks to.</summary>
    private static async Task<string?> TicketFor(HttpClient client)
    {
        using var response = await client.PostAsync(TicketsPath, content: null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TicketBody>(Ct);
        return body.ShouldNotBeNull().Ticket;
    }

    private static IDatabase Database(WebApplicationFactory<Program> host)
        => host.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();

    /// <summary>The pub/sub channels Valkey has subscribers for (<c>PUBSUB CHANNELS</c>).</summary>
    private static async Task<List<string>> ChannelsAsync(WebApplicationFactory<Program> host)
    {
        var reply = await Database(host).ExecuteAsync("PUBSUB", "CHANNELS");
        return [.. ((RedisResult[])reply!).Select(channel => (string)channel!)];
    }

    /// <summary>How many subscribers Valkey counts on <paramref name="channel"/> (<c>PUBSUB NUMSUB</c>), read in real time.</summary>
    private static async Task<long> SubscribersAsync(WebApplicationFactory<Program> host, string channel)
    {
        try
        {
            var reply = (RedisResult[])(await Database(host).ExecuteAsync("PUBSUB", "NUMSUB", channel))!;
            return (long)reply[1];
        }
        catch (RedisException)
        {
            return -1;
        }
    }

    private async Task<InAppNotification> InAppRowAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>()
            .Set<InAppNotification>().AsNoTracking().SingleAsync(row => row.UserId == userId, Ct);
    }

    private async Task<Delivery> DeliveryAsync(Guid deliveryId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>()
            .Set<Delivery>().AsNoTracking().SingleAsync(delivery => delivery.Id == deliveryId, Ct);
    }

    private sealed record TicketBody(string Ticket, DateTimeOffset ExpiresAt);
}
