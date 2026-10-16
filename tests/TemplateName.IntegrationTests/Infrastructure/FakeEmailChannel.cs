using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// The email channel of the integration test hosts until the real one exists: it keeps every call in memory instead of sending. It
/// counts the calls per delivery id, fails on demand (<see cref="FailNext"/>, <see cref="FailAlways"/>) and can park one call
/// (<see cref="Hold"/>) so a test can let a lease run out, or stop the host, mid-send. A parked call ignores cancellation: it stands for a
/// server that has already accepted the message. Thread-safe, because dispatchers send concurrently.
/// </summary>
internal sealed class FakeEmailChannel : INotificationChannel
{
    private readonly Lock _lock = new();
    private readonly List<DeliveryWork> _calls = [];
    private readonly Queue<ChannelSendResult> _nextResults = new();
    private ChannelSendResult? _alwaysResult;
    private TaskCompletionSource<Guid>? _entered;
    private TaskCompletionSource? _release;

    public NotificationChannel Channel => NotificationChannel.Email;

    /// <summary>A snapshot of every call so far (sent or failed), oldest first.</summary>
    public IReadOnlyList<DeliveryWork> Calls
    {
        get
        {
            lock (_lock)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The number of calls for <paramref name="deliveryId"/>.</summary>
    public int CallsFor(Guid deliveryId)
    {
        lock (_lock)
        {
            return _calls.Count(work => work.DeliveryId == deliveryId);
        }
    }

    /// <summary>Makes the next <paramref name="times"/> calls return <paramref name="result"/> instead of <see cref="ChannelSendResult.Sent"/>.</summary>
    public void FailNext(ChannelSendResult result, int times = 1)
    {
        lock (_lock)
        {
            for (var i = 0; i < times; i++)
            {
                _nextResults.Enqueue(result);
            }
        }
    }

    /// <summary>Makes every call return <paramref name="result"/> until <see cref="Reset"/>.</summary>
    public void FailAlways(ChannelSendResult result)
    {
        lock (_lock)
        {
            _alwaysResult = result;
        }
    }

    /// <summary>Parks the next call until <see cref="Release"/>; the returned task completes with that call's delivery id.</summary>
    public Task<Guid> Hold()
    {
        lock (_lock)
        {
            _entered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
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

    public void Reset()
    {
        lock (_lock)
        {
            _calls.Clear();
            _nextResults.Clear();
            _alwaysResult = null;
            _release?.TrySetResult();
            _entered = null;
            _release = null;
        }
    }

    public async Task<ChannelSendResult> SendAsync(DeliveryWork work, CancellationToken cancellationToken)
    {
        ChannelSendResult result;
        Task held = Task.CompletedTask;
        lock (_lock)
        {
            _calls.Add(work);
            result = _nextResults.TryDequeue(out var next) ? next : _alwaysResult ?? ChannelSendResult.Sent;

            if (_entered is { } entered && _release is { } release)
            {
                entered.TrySetResult(work.DeliveryId);
                held = release.Task;
                _entered = null;
            }
        }

        // Not cancellable: a parked call models a message the server has already accepted.
        await held;
        return result;
    }
}
