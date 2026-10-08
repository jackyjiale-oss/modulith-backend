using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace TemplateName.IntegrationTests.Auth;

/// <summary>
/// Runs Mailpit (a disposable SMTP server with a REST API) in a container. The image tag is pinned and matches the one in
/// <c>docker-compose.yml</c>; a test keeps the two in step.
/// </summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    /// <summary>The pinned image. Change it together with <c>docker-compose.yml</c>.</summary>
    public const string Image = "axllent/mailpit:v1.31.4";

    private const int SmtpContainerPort = 1025;
    private const int HttpContainerPort = 8025;

    private readonly IContainer _container = new ContainerBuilder(Image)
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
