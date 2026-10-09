using TemplateName.Application.Common.Messaging;

namespace TemplateName.Infrastructure.Common.Messaging;

/// <summary>The scoped <see cref="IOutboxMessageContext"/>: empty until <c>OutboxDispatcher</c> sets it in a message's scope.</summary>
internal sealed class OutboxMessageContext : IOutboxMessageContext
{
    private (Guid MessageId, DateTimeOffset OccurredAt)? _message;

    public Guid MessageId => Current.MessageId;

    public DateTimeOffset OccurredAt => Current.OccurredAt;

    private (Guid MessageId, DateTimeOffset OccurredAt) Current
        => _message ?? throw new InvalidOperationException(
            "IOutboxMessageContext is only available to domain event handlers during an outbox dispatch.");

    /// <summary>Sets the message this scope dispatches; a scope dispatches one message, so it can be set only once.</summary>
    internal void Set(Guid messageId, DateTimeOffset occurredAt)
    {
        if (_message is not null)
        {
            throw new InvalidOperationException($"This scope already dispatches outbox message {_message.Value.MessageId}.");
        }

        _message = (messageId, occurredAt);
    }
}
