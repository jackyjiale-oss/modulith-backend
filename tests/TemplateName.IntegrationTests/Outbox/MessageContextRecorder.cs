namespace TemplateName.IntegrationTests.Outbox;

/// <summary>Collects the outbox message ids and times <see cref="MessageContextTestEventHandler"/> read from its message context.</summary>
public sealed class MessageContextRecorder
{
    private readonly Lock _lock = new();
    private readonly List<(Guid MessageId, DateTimeOffset OccurredAt)> _messages = [];

    public IReadOnlyList<(Guid MessageId, DateTimeOffset OccurredAt)> Messages
    {
        get
        {
            lock (_lock)
            {
                return [.. _messages];
            }
        }
    }

    public void Record(Guid messageId, DateTimeOffset occurredAt)
    {
        lock (_lock)
        {
            _messages.Add((messageId, occurredAt));
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _messages.Clear();
        }
    }
}
