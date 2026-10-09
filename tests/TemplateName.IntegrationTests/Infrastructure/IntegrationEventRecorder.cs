using TemplateName.SharedKernel;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Collects the integration events <see cref="RecordingIntegrationEventHandler{TEvent}"/> receives, in order. Thread-safe, because
/// concurrent outbox dispatchers publish into it.
/// </summary>
public sealed class IntegrationEventRecorder
{
    private readonly Lock _lock = new();
    private readonly List<IIntegrationEvent> _events = [];

    /// <summary>A snapshot of the events received so far, oldest first.</summary>
    public IReadOnlyList<IIntegrationEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return [.. _events];
            }
        }
    }

    public void Record(IIntegrationEvent integrationEvent)
    {
        lock (_lock)
        {
            _events.Add(integrationEvent);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _events.Clear();
        }
    }
}
