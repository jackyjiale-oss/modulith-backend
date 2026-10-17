using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary>
/// Runs Mailpit (a disposable SMTP server with a REST API) in a container. The image tag is pinned and matches the one in
/// <c>docker-compose.yml</c>; a test keeps the two in step. It accepts only recipients at <c>example.com</c>
/// (<c>MP_SMTP_ALLOWED_RECIPIENTS</c>) and rejects every other one at <c>RCPT TO</c>, the way a real server rejects an unknown mailbox.
/// Its chaos feature (<c>MP_ENABLE_CHAOS</c>) is on, so a test can make it refuse the sender (<see cref="RefuseSendersAsync"/>).
/// </summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    /// <summary>The pinned image. Change it together with <c>docker-compose.yml</c>.</summary>
    public const string Image = "axllent/mailpit:v1.31.4";

    /// <summary>An address the server rejects at <c>RCPT TO</c>: it is not at <c>example.com</c>.</summary>
    public const string RejectedRecipient = "nobody@rejected.example.net";

    private const int SmtpContainerPort = 1025;
    private const int HttpContainerPort = 8025;

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithEnvironment("MP_SMTP_ALLOWED_RECIPIENTS", @"@example\.com$")
        .WithEnvironment("MP_ENABLE_CHAOS", "true")
        .WithPortBinding(SmtpContainerPort, assignRandomHostPort: true)
        .WithPortBinding(HttpContainerPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(HttpContainerPort).ForPath("/livez")))
        .Build();

    private HttpClient? _http;

    /// <summary>The host the SMTP port is published on.</summary>
    public string Host => _container.Hostname;

    /// <summary>The host port mapped to Mailpit's SMTP port.</summary>
    public int SmtpPort => _container.GetMappedPublicPort(SmtpContainerPort);

    private HttpClient Http => _http ?? throw new InvalidOperationException("The Mailpit container has not been started.");

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);
        _http = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(HttpContainerPort)}/") };
    }

    public async ValueTask DisposeAsync()
    {
        _http?.Dispose();
        await _container.DisposeAsync();
    }

    /// <summary>Deletes every message Mailpit holds.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        using var response = await Http.DeleteAsync("api/v1/messages", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Makes Mailpit answer every <c>MAIL FROM</c> with <paramref name="errorCode"/> (<c>PUT /api/v1/chaos</c>), or, with
    /// <see langword="null"/>, accept senders again. Chaos is server-wide: reset it before the test ends.
    /// </summary>
    public async Task RefuseSendersAsync(int? errorCode, CancellationToken cancellationToken)
    {
        var triggers = new
        {
            Sender = new { ErrorCode = errorCode ?? 451, Probability = errorCode is null ? 0 : 100 },
            Recipient = new { ErrorCode = 451, Probability = 0 },
            Authentication = new { ErrorCode = 535, Probability = 0 },
        };
        using var response = await Http.PutAsJsonAsync("api/v1/chaos", triggers, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>The summaries of the stored messages, newest first (<c>GET /api/v1/messages</c>).</summary>
    public async Task<IReadOnlyList<MailpitSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var list = await Http.GetFromJsonAsync<MailpitList>("api/v1/messages", cancellationToken);
        return list?.Messages ?? [];
    }

    /// <summary>One stored message with its text and HTML parts (<c>GET /api/v1/message/{id}</c>).</summary>
    public async Task<MailpitMessage> GetAsync(string id, CancellationToken cancellationToken)
    {
        return await Http.GetFromJsonAsync<MailpitMessage>($"api/v1/message/{id}", cancellationToken)
            ?? throw new InvalidOperationException($"Mailpit returned no message '{id}'.");
    }

    public sealed record MailpitAddress(
        [property: JsonPropertyName("Name")] string Name,
        [property: JsonPropertyName("Address")] string Address);

    public sealed record MailpitSummary(
        [property: JsonPropertyName("ID")] string Id,
        [property: JsonPropertyName("Subject")] string Subject);

    public sealed record MailpitMessage(
        [property: JsonPropertyName("ID")] string Id,
        [property: JsonPropertyName("Subject")] string Subject,
        [property: JsonPropertyName("From")] MailpitAddress From,
        [property: JsonPropertyName("To")] IReadOnlyList<MailpitAddress> To,
        [property: JsonPropertyName("Text")] string Text,
        [property: JsonPropertyName("HTML")] string Html);

    private sealed record MailpitList([property: JsonPropertyName("messages")] IReadOnlyList<MailpitSummary> Messages);
}
