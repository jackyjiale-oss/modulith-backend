namespace TemplateName.Modules.Notifications.Infrastructure.Deliveries;

/// <summary>
/// Work a channel wants done only once the delivery's outcome is saved, such as the in-app live push: scoped to one delivery attempt in
/// the worker. The worker runs the callbacks after a successful save and drops them when the save does not happen (a lost lease).
/// </summary>
internal sealed class DeliverySaveCallbacks
{
    private readonly List<Func<CancellationToken, Task>> _callbacks = [];

    /// <summary>Adds <paramref name="callback"/>; it must handle its own failures (it is best effort).</summary>
    public void Add(Func<CancellationToken, Task> callback) => _callbacks.Add(callback);

    /// <summary>Runs the callbacks in the order they were added, once.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var callback in _callbacks)
        {
            await callback(cancellationToken);
        }

        _callbacks.Clear();
    }
}
