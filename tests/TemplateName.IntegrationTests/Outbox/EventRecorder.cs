using TemplateName.SharedKernel;

namespace TemplateName.IntegrationTests.Outbox;

/// <summary>Collects the domain events that test handlers receive. Thread-safe, because concurrent dispatchers record into it.</summary>
public sealed class EventRecorder
{
    private readonly Lock _lock = new();
    private readonly List<IDomainEvent> _events = [];

    public IReadOnlyList<IDomainEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return [.. _events];
            }
        }
    }

    public void Record(IDomainEvent domainEvent)
    {
        lock (_lock)
        {
            _events.Add(domainEvent);
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
