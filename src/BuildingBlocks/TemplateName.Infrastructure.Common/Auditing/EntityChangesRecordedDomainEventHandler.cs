using TemplateName.Application.Common.Auditing;
using TemplateName.Application.Common.Messaging;
using TemplateName.SharedKernel.Auditing;

namespace TemplateName.Infrastructure.Common.Auditing;

/// <summary>
/// Run by the outbox of every module: publishes the change set of one save as <see cref="EntityChangesRecordedIntegrationEvent"/>
/// for the Audit module. The id and time are the outbox message's (ADR 0018), so the id is also the change set's and a retry
/// republishes the same one; a failed publish (a consumer failed) throws, and the outbox retries.
/// </summary>
internal sealed class EntityChangesRecordedDomainEventHandler(
    IOutboxMessageContext messageContext,
    IIntegrationEventPublisher publisher) : IDomainEventHandler<EntityChangesRecordedDomainEvent>
{
    public Task HandleAsync(EntityChangesRecordedDomainEvent domainEvent, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            new EntityChangesRecordedIntegrationEvent(
                messageContext.MessageId,
                messageContext.OccurredAt,
                domainEvent.UserId,
                domainEvent.SessionId,
                domainEvent.TraceId,
                domainEvent.Module,
                domainEvent.Changes),
            cancellationToken);
}
