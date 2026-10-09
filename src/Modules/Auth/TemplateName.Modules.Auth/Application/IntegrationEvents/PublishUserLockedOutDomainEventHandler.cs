using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Users.Events;

namespace TemplateName.Modules.Auth.Application.IntegrationEvents;

/// <summary>
/// Run by the outbox: publishes <see cref="UserLockedOutIntegrationEvent"/> when failed sign-ins lock an account, with the lockout's
/// end. The id and time are the outbox message's (ADR 0018), so a retry republishes the same id; a failed publish (a consumer failed)
/// throws, and the outbox retries.
/// </summary>
internal sealed class PublishUserLockedOutDomainEventHandler(
    IIntegrationEventPublisher publisher,
    IOutboxMessageContext messageContext) : IDomainEventHandler<UserLockedOutDomainEvent>
{
    public Task HandleAsync(UserLockedOutDomainEvent domainEvent, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            new UserLockedOutIntegrationEvent(
                messageContext.MessageId,
                messageContext.OccurredAt,
                domainEvent.UserId,
                domainEvent.LockoutEnd),
            cancellationToken);
}
