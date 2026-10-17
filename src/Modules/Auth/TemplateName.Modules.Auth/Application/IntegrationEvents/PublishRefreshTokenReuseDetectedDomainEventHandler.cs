using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Sessions.Events;

namespace TemplateName.Modules.Auth.Application.IntegrationEvents;

/// <summary>
/// Run by the outbox: publishes <see cref="RefreshTokenReuseDetectedIntegrationEvent"/> when a reused refresh token revoked its
/// session, with the session's id. The id and time are the outbox message's (ADR 0018), so a retry republishes the same id; a failed
/// publish (a consumer failed) throws, and the outbox retries.
/// </summary>
internal sealed class PublishRefreshTokenReuseDetectedDomainEventHandler(
    IIntegrationEventPublisher publisher,
    IOutboxMessageContext messageContext) : IDomainEventHandler<RefreshTokenReuseDetectedDomainEvent>
{
    public Task HandleAsync(RefreshTokenReuseDetectedDomainEvent domainEvent, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            new RefreshTokenReuseDetectedIntegrationEvent(
                messageContext.MessageId,
                messageContext.OccurredAt,
                domainEvent.UserId,
                domainEvent.SessionId),
            cancellationToken);
}
