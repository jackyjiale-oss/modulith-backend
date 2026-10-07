namespace TemplateName.IntegrationTests.Outbox;

/// <summary>
/// Holds the first <see cref="GateTestEventHandler"/> call after <see cref="Close"/> until <see cref="Open"/>; every other call passes
/// straight through. Lets a test park a dispatcher inside a message without sleeping.
/// </summary>
public sealed class HandlerGate
{
    private readonly Lock _lock = new();
    private TaskCompletionSource<Guid>? _entered;
    private TaskCompletionSource? _release;
    private bool _armed;

    /// <summary>Arms the gate; the returned task completes with the event id of the call it holds.</summary>
    public Task<Guid> Close()
    {
        lock (_lock)
        {
            _entered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _armed = true;
            return _entered.Task;
        }
    }

    public void Open()
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
            _armed = false;
            _release?.TrySetResult();
            _entered = null;
            _release = null;
        }
    }

    internal Task PassAsync(Guid eventId, CancellationToken cancellationToken)
    {
        TaskCompletionSource release;
        lock (_lock)
        {
            if (!_armed)
            {
                return Task.CompletedTask;
            }

            _armed = false;
            _entered!.TrySetResult(eventId);
            release = _release!;
        }

        return release.Task.WaitAsync(cancellationToken);
    }
}
