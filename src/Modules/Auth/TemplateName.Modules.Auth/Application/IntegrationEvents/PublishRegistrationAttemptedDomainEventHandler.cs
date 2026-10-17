using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Users.Events;

namespace TemplateName.Modules.Auth.Application.IntegrationEvents;

/// <summary>
/// Run by the outbox: publishes <see cref="RegistrationAttemptedIntegrationEvent"/> when someone registers an address that already has
/// an account. It carries only the owner's id; the consumer looks up where and how to tell them. The id and time are the outbox
/// message's (ADR 0018), so a retry republishes the same id; a failed publish (a consumer failed) throws, and the outbox retries.
/// </summary>
internal sealed class PublishRegistrationAttemptedDomainEventHandler(
    IIntegrationEventPublisher publisher,
    IOutboxMessageContext messageContext) : IDomainEventHandler<RegistrationAttemptedDomainEvent>
{
    public Task HandleAsync(RegistrationAttemptedDomainEvent domainEvent, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            new RegistrationAttemptedIntegrationEvent(
                messageContext.MessageId,
                messageContext.OccurredAt,
                domainEvent.UserId),
            cancellationToken);
}
