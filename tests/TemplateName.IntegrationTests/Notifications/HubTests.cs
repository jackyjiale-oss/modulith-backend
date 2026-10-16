using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using Serilog.Core;
using TemplateName.Application.Common.Messaging;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.HubTickets;
using TemplateName.Modules.Notifications.Domain.HubTickets;
using TemplateName.Modules.Notifications.Domain.InApp;
using TemplateName.Modules.Notifications.Infrastructure.Persistence;
using TemplateName.Web.Common.Observability;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary>
/// The notification hub through the real host: tickets from <c>POST /api/v1/notifications/hub-tickets</c>, the <c>HubTicket</c> scheme on
/// <c>/hubs/notifications</c>, and live pushes from the delivery worker (run by hand). The connections use the SignalR .NET client with the
/// WebSockets transport only. On the test server, its <c>WebSocketFactory</c> does what a browser does (the JavaScript client, or the .NET
/// client on WebAssembly): it asks the <c>AccessTokenProvider</c> again and sends that value as <c>access_token</c> in the query string,
/// because the browser WebSocket API cannot set headers. The negotiate request goes through the client's own handler, which sends the
/// provider's value as <c>Authorization: Bearer</c>. One test runs the .NET client unchanged against Kestrel.
/// </summary>
/// <remarks>
/// SignalR closes a connection whose authentication has expired by the <b>system</b> clock (its connection manager compares with
/// <c>DateTimeOffset.UtcNow</c>, not <see cref="TimeProvider"/>), so every test starts with the host's fake clock at the real time: the
/// ticket principal's expiry is then on the timeline SignalR checks.
/// </remarks>
public sealed class HubTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string HubPath = "/hubs/notifications";
    private const string NegotiatePath = "/hubs/notifications/negotiate?negotiateVersion=1";
    private const string TicketsPath = "/api/v1/notifications/hub-tickets";
    private const string NotificationReceived = "notificationReceived";

    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Whole seconds, like the factory's start instant, so values compare equal after the database's datetime2(3) rounding.
        var now = TimeProvider.System.GetUtcNow();
        Factory.Time.SetUtcNow(new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero));
    }

    [Fact]
    public async Task Connected_client_receives_its_in_app_notification()
    {
        var user = await SignInAsync();
        var calls = 0;
        await using var connection = Connect(Factory.Server, () =>
        {
            Interlocked.Increment(ref calls);
            return TicketFor(Client);
        });
        var received = Receive(connection);

        await connection.StartAsync(Ct);

        // A fresh ticket for each of the two requests a connection makes (negotiate, then the WebSocket), both consumed.
        connection.State.ShouldBe(HubConnectionState.Connected);
        calls.ShouldBe(2);
        (await TicketsAsync()).ShouldAllBe(ticket => ticket.ConsumedAt != null);
        (await TicketsAsync()).Count.ShouldBe(2);

        await PublishPasswordChangedAsync(user.UserId);
        await DispatchNotificationsAsync();

        var payload = await ReadAsync(received);
        var row = await InAppRowAsync(user.UserId);
        payload.GetProperty("id").GetGuid().ShouldBe(row.Id);
        payload.GetProperty("typeCode").GetString().ShouldBe("auth.password_changed");
        payload.GetProperty("category").GetString().ShouldBe("Security");
        payload.GetProperty("title").GetString().ShouldBe(row.Title);
        payload.GetProperty("body").GetString().ShouldBe(row.Body);
        payload.GetProperty("createdAt").GetDateTime().ShouldBe(row.CreatedAt);
    }

    [Fact]
    public async Task Another_users_notification_is_not_received()
    {
        var other = await CreateUserAsync("bob@example.com");
        var user = await SignInAsync();
        await using var connection = Connect(Factory.Server, () => TicketFor(Client));
        var received = Receive(connection);
        await connection.StartAsync(Ct);

        // The other user's push is sent first; a push reaches a connection in order, so it would arrive before the user's own.
        await PublishPasswordChangedAsync(other.Id);
        await DispatchNotificationsAsync();
        await PublishPasswordChangedAsync(user.UserId);
        await DispatchNotificationsAsync();

        var payload = await ReadAsync(received);
        payload.GetProperty("id").GetGuid().ShouldBe((await InAppRowAsync(user.UserId)).Id);
        received.TryRead(out _).ShouldBeFalse();
        Factory.InAppPusher.Pushes.Select(push => push.UserId).ShouldBe([other.Id, user.UserId]);
    }

    [Fact]
    public async Task Expired_ticket_cannot_connect()
    {
        await SignInAsync();
        var used = await IssueTicketAsync(Client);
        var expired = await IssueTicketAsync(Client);
        (await NegotiateAsync(bearer: used)).StatusCode.ShouldBe(HttpStatusCode.OK);

        Factory.Time.Advance(TimeSpan.FromSeconds(31));

        (await NegotiateAsync(bearer: expired)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await using var connection = Connect(Factory.Server, () => Task.FromResult<string?>(expired));
        var failure = await Should.ThrowAsync<HttpRequestException>(() => connection.StartAsync(Ct));
        failure.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await TicketsAsync()).Count(ticket => ticket.ConsumedAt == null).ShouldBe(1);
    }

    [Fact]
    public async Task Reused_ticket_cannot_connect()
    {
        await SignInAsync();
        var ticket = await IssueTicketAsync(Client);

        // The client reuses one ticket for both requests: negotiate consumes it, so the WebSocket request is refused.
        await using var connection = Connect(Factory.Server, () => Task.FromResult<string?>(ticket));
        var failure = await Should.ThrowAsync<Exception>(() => connection.StartAsync(Ct));

        failure.Message.ShouldContain("401");
        connection.State.ShouldBe(HubConnectionState.Disconnected);
        (await NegotiateAsync(bearer: ticket)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var stored = (await TicketsAsync()).ShouldHaveSingleItem();
        stored.ConsumedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Jwt_in_query_string_is_rejected()
    {
        var user = await SignInAsync();

        // In the header, the JWT bearer scheme authenticates the hub (for clients that can send headers); never from the query.
        (await NegotiateAsync(bearer: user.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await NegotiateAsync(query: user.AccessToken)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var webSocket = Factory.Server.CreateWebSocketClient();
        var refused = await Should.ThrowAsync<InvalidOperationException>(
            () => webSocket.ConnectAsync(new Uri(Factory.Server.BaseAddress, $"hubs/notifications?access_token={Uri.EscapeDataString(user.AccessToken)}"), Ct));
        refused.Message.ShouldContain("401");
    }

    [Fact]
    public async Task Jwt_bearer_handler_does_not_read_the_query_string()
    {
        var options = Factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);
        var context = new DefaultHttpContext();
        context.Request.Path = HubPath;
        context.Request.QueryString = QueryString.Create("access_token", "a.b.c");
        var messageReceived = new MessageReceivedContext(
            context, new Microsoft.AspNetCore.Authentication.AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler)), options);

        // No custom events type and no OnMessageReceived that sets the token: the handler reads the Authorization header only.
        options.EventsType.ShouldBeNull();
        await (options.Events ?? new JwtBearerEvents()).MessageReceived(messageReceived);

        messageReceived.Token.ShouldBeNull();
    }

    [Fact]
    public async Task Ticket_endpoint_requires_a_token_and_is_no_store()
    {
        using (var anonymous = await Client.PostAsync(TicketsPath, content: null, Ct))
        {
            anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var user = await SignInAsync();
        using var response = await Client.PostAsync(TicketsPath, content: null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        response.Headers.Pragma.ToString().ShouldBe("no-cache");
        var body = (await response.Content.ReadFromJsonAsync<TicketBody>(Ct)).ShouldNotBeNull();
        body.Ticket.Length.ShouldBe(43);
        body.ExpiresAt.ShouldBe(Factory.Time.GetUtcNow().AddSeconds(30));
        var bytes = Base64Url.DecodeFromChars(body.Ticket);
        bytes.Length.ShouldBe(32);

        // Only the SHA-256 of the ticket's bytes is stored.
        var stored = (await TicketsAsync()).ShouldHaveSingleItem();
        stored.UserId.ShouldBe(user.UserId);
        stored.TokenHash.ShouldBe(SHA256.HashData(bytes));
        stored.ConsumedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Ticket_does_not_authenticate_any_other_endpoint()
    {
        await SignInAsync();
        var ticket = await IssueTicketAsync(Client);
        SignOut();

        using var withHeader = new HttpRequestMessage(HttpMethod.Get, "/api/v1/notifications/inbox").WithBearer(ticket);
        (await Client.SendAsync(withHeader, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Client.GetAsync($"/api/v1/notifications/inbox?access_token={ticket}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Client.PostAsync($"{TicketsPath}?access_token={ticket}", content: null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The scheme never looked at those requests, so the ticket is still unused.
        (await TicketsAsync()).ShouldHaveSingleItem().ConsumedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Ticket_endpoint_limits_each_user()
    {
        var limit = Factory.Services.GetRequiredService<IOptions<HubOptions>>().Value.TicketsPerMinute;
        await SignInAsync();
        for (var i = 0; i < limit; i++)
        {
            await IssueTicketAsync(Client);
        }

        using var refused = await Client.PostAsync(TicketsPath, content: null, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("rate_limit.exceeded");

        // The limit is per user: someone else still gets a ticket.
        await SignInAsync();
        await IssueTicketAsync(Client);
        (await TicketsAsync()).Count.ShouldBe(limit + 1);
    }

    [Fact]
    public async Task Connection_closes_when_the_ticket_principal_expires()
    {
        // The principal expires ConnectionLifetime after the ticket is consumed by the host's clock; set that clock so the expiry falls
        // two seconds from the real now, which SignalR's scan (once a second, on the system clock) compares with.
        var lifetime = Factory.Services.GetRequiredService<IOptions<HubOptions>>().Value.ConnectionLifetime;
        await SignInAsync();
        Factory.Time.AdjustTime(TimeProvider.System.GetUtcNow() - lifetime + TimeSpan.FromSeconds(2));
        await RenewAccessTokenAsync();
        await using var connection = Connect(Factory.Server, () => TicketFor(Client));
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        await connection.StartAsync(Ct);
        connection.State.ShouldBe(HubConnectionState.Connected);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        connection.State.ShouldBe(HubConnectionState.Disconnected);
    }

    [Fact]
    public async Task Dotnet_client_fetches_a_new_ticket_for_negotiate_and_for_the_websocket()
    {
        // The SignalR .NET client exactly as an application uses it, over a real socket: its own HTTP handler and WebSocket factory.
        await using var host = Factory.WithWebHostBuilder(_ => { });
        host.UseKestrel(0);
        host.StartServer();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        await SignInAsync();
        using var client = await CreateClientAsync(host);
        client.BaseAddress = new Uri(address);
        var calls = 0;
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(address), HubPath), HttpTransportType.WebSockets, options => options.AccessTokenProvider = () =>
            {
                Interlocked.Increment(ref calls);
                return TicketFor(client);
            })
            .Build();

        await connection.StartAsync(Ct);

        connection.State.ShouldBe(HubConnectionState.Connected);
        calls.ShouldBe(2);
        var tickets = await TicketsAsync();
        tickets.Count.ShouldBe(2);
        tickets.ShouldAllBe(ticket => ticket.ConsumedAt != null);
    }

    [Fact]
    public async Task Ticket_value_is_not_logged_or_traced()
    {
        // A host that logs everything ASP.NET Core and the application write, at every level, through its real Serilog pipeline into a
        // recording sink, and that traces requests with the production ASP.NET Core instrumentation options.
        var sink = new RecordingLogEventSink();
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Enqueue,
        };
        ActivitySource.AddActivityListener(listener);
        await using var host = Factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Serilog:MinimumLevel:Default"] = "Warning",
                ["Serilog:MinimumLevel:Override:Microsoft.AspNetCore"] = "Verbose",
                ["Serilog:MinimumLevel:Override:Serilog.AspNetCore"] = "Verbose",
                ["Serilog:MinimumLevel:Override:TemplateName"] = "Verbose",
            }));
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<ILogEventSink>(sink);
                services.AddOpenTelemetry().WithTracing(tracing => tracing.AddAspNetCoreInstrumentation(ObservabilityExtensions.ConfigureAspNetCoreTracing));
            });
        });
        await SignInAsync();
        using var client = await CreateClientAsync(host);
        var tickets = new ConcurrentQueue<string>();
        await using var connection = Connect(host.Server, async () =>
        {
            var ticket = await IssueTicketAsync(client);
            tickets.Enqueue(ticket);
            return (string?)ticket;
        });

        await connection.StartAsync(Ct);
        await connection.StopAsync(Ct);

        tickets.Count.ShouldBe(2);

        // The request event of the WebSocket request is written when that request ends, after the connection closed.
        var requestEvents = await EventuallyAsync(
            () => sink.Events.Where(logEvent => logEvent.Properties.TryGetValue("RequestPath", out var path) && path.ToString() == $"\"{HubPath}\"").ToList(),
            events => events.Count >= 1);
        requestEvents.ShouldNotBeEmpty();
        sink.Entries.ShouldContain(entry => entry.Contains("access_token=[Redacted]", StringComparison.Ordinal));
        foreach (var ticket in tickets)
        {
            sink.Entries.ShouldAllBe(entry => !entry.Contains(ticket, StringComparison.Ordinal));
        }

        var hubActivities = activities.Where(activity => Equals(activity.GetTagItem("url.path"), HubPath)).ToList();
        hubActivities.ShouldNotBeEmpty();
        // The instrumentation masks every query value (the connection id too); the host's enrichment then marks the access token.
        hubActivities.Select(activity => activity.GetTagItem("url.query")).ShouldContain("?id=Redacted&access_token=[Redacted]");
        foreach (var ticket in tickets)
        {
            activities.ShouldAllBe(activity => !activity.TagObjects.Any(tag => tag.Value != null && tag.Value.ToString()!.Contains(ticket, StringComparison.Ordinal)));
            activities.ShouldAllBe(activity => !activity.DisplayName.Contains(ticket, StringComparison.Ordinal));
        }
    }

    private static HubConnection Connect(TestServer server, Func<Task<string?>> accessTokenProvider)
        => new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, HubPath.TrimStart('/')), HttpTransportType.WebSockets, options =>
            {
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = accessTokenProvider;
                options.WebSocketFactory = (context, cancellationToken) => ConnectWebSocketAsync(server, context, cancellationToken);
            })
            .Build();

    /// <summary>What a browser client does: a fresh provider call, sent as <c>access_token</c> because a browser WebSocket has no headers.</summary>
    private static async ValueTask<WebSocket> ConnectWebSocketAsync(TestServer server, WebSocketConnectionContext context, CancellationToken cancellationToken)
    {
        var token = await context.Options.AccessTokenProvider!();
        var query = context.Uri.Query.TrimStart('?');
        var uri = new UriBuilder(context.Uri) { Query = $"{query}&access_token={Uri.EscapeDataString(token!)}" }.Uri;

        return await server.CreateWebSocketClient().ConnectAsync(uri, cancellationToken);
    }

    private static ChannelReader<JsonElement> Receive(HubConnection connection)
    {
        var received = Channel.CreateUnbounded<JsonElement>();
        connection.On<JsonElement>(NotificationReceived, payload => received.Writer.TryWrite(payload));
        return received.Reader;
    }

    private static async Task<JsonElement> ReadAsync(ChannelReader<JsonElement> received)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(ReceiveTimeout);
        return await received.ReadAsync(timeout.Token);
    }

    private static async Task<T> EventuallyAsync<T>(Func<T> read, Func<T, bool> done)
    {
        var deadline = Stopwatch.StartNew();
        var value = read();
        while (!done(value) && deadline.Elapsed < ReceiveTimeout)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), Ct);
            value = read();
        }

        return value;
    }

    private static async Task<string> IssueTicketAsync(HttpClient client)
    {
        using var response = await client.PostAsync(TicketsPath.TrimStart('/'), content: null, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TicketBody>(Ct);
        return body.ShouldNotBeNull().Ticket;
    }

    /// <summary>An <c>AccessTokenProvider</c> call: a new ticket each time.</summary>
    private static async Task<string?> TicketFor(HttpClient client) => await IssueTicketAsync(client);

    private async Task<HttpResponseMessage> NegotiateAsync(string? bearer = null, string? query = null)
    {
        var path = query is null ? NegotiatePath : $"{NegotiatePath}&access_token={Uri.EscapeDataString(query)}";
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (bearer is not null)
        {
            request.WithBearer(bearer);
        }

        return await client.SendAsync(request, Ct);
    }

    private async Task PublishPasswordChangedAsync(Guid userId)
        => await Factory.Services.GetRequiredService<IIntegrationEventPublisher>()
            .PublishAsync(new PasswordChangedIntegrationEvent(Guid.NewGuid(), Factory.Time.GetUtcNow(), userId), Ct);

    private async Task<List<HubTicket>> TicketsAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>().Set<HubTicket>().AsNoTracking().ToListAsync(Ct);
    }

    private async Task<InAppNotification> InAppRowAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NotificationsDbContext>()
            .Set<InAppNotification>().AsNoTracking().SingleAsync(row => row.UserId == userId, Ct);
    }

    private sealed record TicketBody(string Ticket, DateTimeOffset ExpiresAt);
}
