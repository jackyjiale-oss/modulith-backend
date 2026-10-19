using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Sessions.Events;

namespace TemplateName.Modules.Auth.Application.IntegrationEvents;

/// <summary>
/// Run by the outbox: publishes <see cref="NewDeviceSignedInIntegrationEvent"/> when a user signs in from a device they have not used
/// before, with the session and the address the server observed, and no client-supplied text. The id and time are the outbox
/// message's (ADR 0018), so a retry republishes the same id; a failed publish (a consumer failed) throws, and the outbox retries.
/// </summary>
internal sealed class PublishNewDeviceSignedInDomainEventHandler(
    IIntegrationEventPublisher publisher,
    IOutboxMessageContext messageContext) : IDomainEventHandler<NewDeviceSignedInDomainEvent>
{
    public Task HandleAsync(NewDeviceSignedInDomainEvent domainEvent, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            new NewDeviceSignedInIntegrationEvent(
                messageContext.MessageId,
                messageContext.OccurredAt,
                domainEvent.UserId,
                domainEvent.SessionId,
                domainEvent.IpAddress),
            cancellationToken);
}
