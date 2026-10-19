using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// The SignalR .NET client against the notification hub of the test server, with the WebSockets transport only. On the test server its
/// <c>WebSocketFactory</c> does what a browser does (the JavaScript client, or the .NET client on WebAssembly): it asks the
/// <c>AccessTokenProvider</c> again and sends that value as <c>access_token</c> in the query string, because the browser WebSocket API
/// cannot set headers. The negotiate request goes through the client's own handler, which sends the provider's value as
/// <c>Authorization: Bearer</c>.
/// </summary>
internal static class NotificationHubClient
{
    private const string HubPath = "hubs/notifications";
    private const string NotificationReceived = "notificationReceived";

    // The upper bound of a wait that ends as soon as its event happens; generous, so a slow runner or a cold first run does not flake.
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A connection (not started) that calls <paramref name="accessTokenProvider"/> once for negotiate and once for the WebSocket, or,
    /// with <paramref name="skipNegotiation"/>, only once: for the WebSocket, its one request.
    /// </summary>
    internal static HubConnection Connect(TestServer server, Func<Task<string?>> accessTokenProvider, bool skipNegotiation = false)
        => new HubConnectionBuilder()
            .WithUrl(new Uri(server.BaseAddress, HubPath), HttpTransportType.WebSockets, options =>
            {
                options.SkipNegotiation = skipNegotiation;
                options.HttpMessageHandlerFactory = _ => server.CreateHandler();
                options.AccessTokenProvider = accessTokenProvider;
                options.WebSocketFactory = (context, cancellationToken) => ConnectWebSocketAsync(server, context, cancellationToken);
            })
            .Build();

    /// <summary>The payloads of the connection's <c>notificationReceived</c> messages, in the order they arrive.</summary>
    internal static ChannelReader<JsonElement> Receive(HubConnection connection)
    {
        var received = Channel.CreateUnbounded<JsonElement>();
        connection.On<JsonElement>(NotificationReceived, payload => received.Writer.TryWrite(payload));
        return received.Reader;
    }

    /// <summary>The next payload; the wait ends as soon as one arrives and fails after <see cref="ReceiveTimeout"/>.</summary>
    internal static async Task<JsonElement> ReadAsync(ChannelReader<JsonElement> received, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(received);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReceiveTimeout);
        return await received.ReadAsync(timeout.Token);
    }

    /// <summary>What a browser client does: a fresh provider call, sent as <c>access_token</c> because a browser WebSocket has no headers.</summary>
    private static async ValueTask<WebSocket> ConnectWebSocketAsync(TestServer server, WebSocketConnectionContext context, CancellationToken cancellationToken)
    {
        var token = await context.Options.AccessTokenProvider!();
        var query = context.Uri.Query.TrimStart('?');
        var uri = new UriBuilder(context.Uri) { Query = $"{query}&access_token={Uri.EscapeDataString(token!)}" }.Uri;

        return await server.CreateWebSocketClient().ConnectAsync(uri, cancellationToken);
    }
}
