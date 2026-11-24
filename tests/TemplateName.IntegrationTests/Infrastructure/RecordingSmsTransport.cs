using System.Collections.Concurrent;
using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// An <see cref="ISmsTransport"/> that records what it is asked to send instead of calling Twilio, as <c>RecordingPushTransport</c> does
/// for push. <see cref="Attempts"/> holds every call, <see cref="Sent"/> the accepted ones; <see cref="FailNextWith"/> makes the next call
/// throw, as Twilio's answer would, and <see cref="MessageId"/> is what an accepted send returns.
/// </summary>
internal sealed class RecordingSmsTransport : ISmsTransport
{
    private readonly ConcurrentQueue<SmsMessage> _attempts = new();
    private readonly ConcurrentQueue<SmsMessage> _sent = new();

    private SmsSendException? _nextFailure;

    public IReadOnlyList<SmsMessage> Attempts => [.. _attempts];

    public IReadOnlyList<SmsMessage> Sent => [.. _sent];

    public string? MessageId { get; set; }

    public void FailNextWith(SmsSendException failure) => _nextFailure = failure;

    public Task<string?> SendAsync(SmsMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        _attempts.Enqueue(message);
        if (Interlocked.Exchange(ref _nextFailure, null) is { } failure)
        {
            throw failure;
        }

        _sent.Enqueue(message);
        return Task.FromResult(MessageId);
    }
}
