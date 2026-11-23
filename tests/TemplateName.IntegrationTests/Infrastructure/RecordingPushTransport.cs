using System.Collections.Concurrent;
using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// An <see cref="IPushTransport"/> that records what it is asked to send instead of calling FCM, as <c>RecordingEmailSender</c> does for
/// email. <see cref="FailNextWith"/> makes the next send throw, as FCM's answer would.
/// </summary>
internal sealed class RecordingPushTransport : IPushTransport
{
    private readonly ConcurrentQueue<PushMessage> _sent = new();

    private PushSendException? _nextFailure;

    public IReadOnlyList<PushMessage> Sent => [.. _sent];

    public void Clear()
    {
        _sent.Clear();
        _nextFailure = null;
    }

    public void FailNextWith(PushSendException failure) => _nextFailure = failure;

    public Task SendAsync(PushMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (Interlocked.Exchange(ref _nextFailure, null) is { } failure)
        {
            throw failure;
        }

        _sent.Enqueue(message);
        return Task.CompletedTask;
    }
}
