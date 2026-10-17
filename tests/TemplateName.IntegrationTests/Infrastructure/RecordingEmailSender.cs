using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// The email transport of the integration test hosts (<see cref="IEmailTransport"/>): the real email channel hands it every message,
/// and it keeps them in memory instead of talking to SMTP. It fails on demand (<see cref="FailNext"/>, <see cref="FailAlways"/>) the way
/// <c>SmtpEmailTransport</c> does, with an <see cref="EmailSendException"/>, and can park one message (<see cref="Hold"/>) so a test can
/// let a lease run out, or stop the host, mid-send. A parked message ignores cancellation: it stands for a server that has already
/// accepted it. Thread-safe, because dispatchers send concurrently.
/// </summary>
internal sealed class RecordingEmailSender : IEmailTransport
{
    private const string TokenKey = "token=";

    private readonly Lock _lock = new();
    private readonly List<EmailMessage> _attempts = [];
    private readonly List<EmailMessage> _sent = [];
    private readonly Queue<EmailSendException> _nextFailures = new();
    private EmailSendException? _alwaysFailure;
    private TaskCompletionSource<string>? _entered;
    private TaskCompletionSource? _release;

    /// <summary>A snapshot of the messages the transport accepted, oldest first.</summary>
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

    /// <summary>A snapshot of every message handed to the transport, accepted or failed, oldest first.</summary>
    public IReadOnlyList<EmailMessage> Attempts
    {
        get
        {
            lock (_lock)
            {
                return [.. _attempts];
            }
        }
    }

    /// <summary>The number of messages handed to the transport for <paramref name="to"/> (compared ignoring case), accepted or failed.</summary>
    public int AttemptsTo(string to)
    {
        lock (_lock)
        {
            return _attempts.Count(message => string.Equals(message.To, to, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Makes the next <paramref name="times"/> messages fail with <paramref name="failure"/> instead of being accepted.</summary>
    public void FailNext(EmailSendException failure, int times = 1)
    {
        lock (_lock)
        {
            for (var i = 0; i < times; i++)
            {
                _nextFailures.Enqueue(failure);
            }
        }
    }

    /// <summary>Makes every message fail with <paramref name="failure"/> until <see cref="Clear"/>.</summary>
    public void FailAlways(EmailSendException failure)
    {
        lock (_lock)
        {
            _alwaysFailure = failure;
        }
    }

    /// <summary>Parks the next message until <see cref="Release"/>; the returned task completes with that message's recipient.</summary>
    public Task<string> Hold()
    {
        lock (_lock)
        {
            _entered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _entered.Task;
        }
    }

    public void Release()
    {
        lock (_lock)
        {
            _release?.TrySetResult();
        }
    }

    /// <summary>Forgets every message and failure and releases a parked message.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _attempts.Clear();
            _sent.Clear();
            _nextFailures.Clear();
            _alwaysFailure = null;
            _release?.TrySetResult();
            _entered = null;
            _release = null;
        }
    }

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        EmailSendException? failure;
        var held = Task.CompletedTask;
        lock (_lock)
        {
            _attempts.Add(message);
            failure = _nextFailures.TryDequeue(out var next) ? next : _alwaysFailure;

            if (_entered is { } entered && _release is { } release)
            {
                entered.TrySetResult(message.To);
                held = release.Task;
                _entered = null;
            }
        }

        // Not cancellable: a parked message models one the server has already accepted.
        await held;
        if (failure is not null)
        {
            throw failure;
        }

        lock (_lock)
        {
            _sent.Add(message);
        }
    }

    /// <summary>
    /// The decoded <c>token=</c> value of the link in the last accepted message to <paramref name="to"/> (compared ignoring case) that
    /// holds one. The link is read from the rendered text part, where the templates put it on a line of its own, URL-encoded by Auth.
    /// </summary>
    /// <exception cref="InvalidOperationException">No accepted message to that address holds a token link.</exception>
    public string LastLinkToken(string to)
    {
        var messages = Sent.Where(sent => string.Equals(sent.To, to, StringComparison.OrdinalIgnoreCase)).ToList();
        if (messages.Count == 0)
        {
            throw new InvalidOperationException($"No email was sent to '{to}'.");
        }

        var text = messages.LastOrDefault(sent => sent.TextBody.Contains(TokenKey, StringComparison.Ordinal))?.TextBody
            ?? throw new InvalidOperationException($"No email to '{to}' holds a '{TokenKey}' link.");

        var start = text.IndexOf(TokenKey, StringComparison.Ordinal) + TokenKey.Length;
        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] != '&')
        {
            end++;
        }

        return Uri.UnescapeDataString(text[start..end]);
    }
}
