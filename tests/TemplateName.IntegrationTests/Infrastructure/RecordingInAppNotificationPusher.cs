using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// The in-app pusher of the integration test hosts: it records every push instead of sending it, and throws on demand
/// (<see cref="FailWith"/>). Thread-safe.
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
}
