using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// The in-app pusher of the integration test hosts: it records every push, throws on demand (<see cref="FailWith"/>) and, through
/// <see cref="ForwardingTo"/>, then hands the push to the host's real SignalR pusher. Thread-safe.
/// </summary>
internal sealed class RecordingInAppNotificationPusher : IInAppNotificationPusher
{
    private readonly Lock _lock = new();
    private readonly List<(Guid UserId, InAppNotificationPushed Payload)> _pushes = [];
    private Exception? _failure;

    /// <summary>A snapshot of the pushes so far, oldest first.</summary>
    public IReadOnlyList<(Guid UserId, InAppNotificationPushed Payload)> Pushes
    {
        get
        {
            lock (_lock)
            {
                return [.. _pushes];
            }
        }
    }

    /// <summary>Makes every push throw <paramref name="exception"/> until <see cref="Reset"/>.</summary>
    public void FailWith(Exception exception)
    {
        lock (_lock)
        {
            _failure = exception;
        }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _pushes.Clear();
            _failure = null;
        }
    }

    public Task PushAsync(Guid userId, InAppNotificationPushed payload, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_failure is not null)
            {
                throw _failure;
            }

            _pushes.Add((userId, payload));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A pusher for one host that records each push here (failing as <see cref="FailWith"/> says) and then hands it to
    /// <paramref name="live"/>, the host's real SignalR pusher, so hub clients of that host receive it too.
    /// </summary>
    public IInAppNotificationPusher ForwardingTo(IInAppNotificationPusher live) => new Forwarding(this, live);

    private sealed class Forwarding(RecordingInAppNotificationPusher recorder, IInAppNotificationPusher live) : IInAppNotificationPusher
    {
        public async Task PushAsync(Guid userId, InAppNotificationPushed payload, CancellationToken cancellationToken)
        {
            await recorder.PushAsync(userId, payload, cancellationToken);
            await live.PushAsync(userId, payload, cancellationToken);
        }
    }
}
