using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Users.Events;

namespace TemplateName.Modules.Auth.Application.IntegrationEvents;

/// <summary>
/// Run by the outbox: publishes <see cref="PasswordChangedIntegrationEvent"/> when a password is changed or reset, with the user's id
/// only. The id and time are the outbox message's (ADR 0018), so a retry republishes the same id; a failed publish (a consumer failed)
/// throws, and the outbox retries.
/// </summary>
internal sealed class PublishPasswordChangedDomainEventHandler(
    IIntegrationEventPublisher publisher,
    IOutboxMessageContext messageContext) : IDomainEventHandler<PasswordChangedDomainEvent>
{
    public Task HandleAsync(PasswordChangedDomainEvent domainEvent, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            new PasswordChangedIntegrationEvent(
                messageContext.MessageId,
                messageContext.OccurredAt,
                domainEvent.UserId),
            cancellationToken);
}
