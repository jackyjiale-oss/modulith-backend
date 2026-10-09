using TemplateName.SharedKernel;

namespace TemplateName.Application.Common.Messaging;

/// <summary>
/// Hands an integration event to every <see cref="IIntegrationEventHandler{TEvent}"/> of its type. Call it from a domain event handler
/// (that is, from the module outbox), never from a request: a failure then makes the outbox retry the publish (ADR 0018).
/// </summary>
public interface IIntegrationEventPublisher
{
    /// <summary>
    /// Runs every handler of <typeparamref name="TEvent"/>, one after another, each created and run in its own DI scope. Every handler
    /// runs even when an earlier one throws or cannot be created; afterwards a single failure is rethrown as it is and several as an
    /// <see cref="AggregateException"/>. Cancellation stops at once and is rethrown unwrapped.
    /// </summary>
    Task PublishAsync<TEvent>(TEvent integrationEvent, CancellationToken cancellationToken)
        where TEvent : IIntegrationEvent;
}
