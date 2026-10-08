using TemplateName.SharedKernel;

namespace TemplateName.Application.Common.Messaging;

/// <summary>Reacts to a domain event after the aggregate that raised it has been saved.</summary>
/// <typeparam name="TEvent">The domain event type.</typeparam>
public interface IDomainEventHandler<in TEvent>
    where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken cancellationToken);
}
