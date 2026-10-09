using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>An <see cref="IEmailSender"/> that keeps every message in memory instead of sending it. Thread-safe, because outbox dispatchers send concurrently.</summary>
internal sealed class RecordingEmailSender : IEmailSender
{
    private const string TokenKey = "token=";

    private readonly Lock _lock = new();
    private readonly List<EmailMessage> _sent = [];

    /// <summary>A snapshot of the messages sent so far, oldest first.</summary>
    public IReadOnlyList<EmailMessage> Sent
    {
        get
        {
            lock (_lock)
            {
                return [.. _sent];
            }
        }
    }

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _sent.Add(message);
        }

        return Task.CompletedTask;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _sent.Clear();
        }
    }

    /// <summary>The decoded <c>token=</c> value of the link in the last message sent to <paramref name="to"/> (compared ignoring case).</summary>
    /// <exception cref="InvalidOperationException">No message went to that address, or it holds no token link.</exception>
    public string LastLinkToken(string to)
    {
        var message = Sent.LastOrDefault(sent => string.Equals(sent.To, to, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No email was sent to '{to}'.");

        var start = message.TextBody.IndexOf(TokenKey, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException($"The last email to '{to}' holds no '{TokenKey}' link.");
        }

        start += TokenKey.Length;
        var end = start;
        while (end < message.TextBody.Length && !char.IsWhiteSpace(message.TextBody[end]) && message.TextBody[end] != '&')
        {
            end++;
        }

        return Uri.UnescapeDataString(message.TextBody[start..end]);
    }
}
