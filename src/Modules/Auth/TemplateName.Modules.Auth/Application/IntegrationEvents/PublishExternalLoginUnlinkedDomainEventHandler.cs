using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Users.Events;

namespace TemplateName.Modules.Auth.Application.IntegrationEvents;

/// <summary>
/// Run by the outbox: publishes <see cref="ExternalLoginUnlinkedIntegrationEvent"/> when a social-login provider is removed from a
/// user's account, with the user's id, the provider's key and the name to show a person (<see cref="IExternalProviderCatalog.DisplayNameOf"/>,
/// the key itself when the provider is no longer configured: a switched-off provider's login can still be removed). The id and time
/// are the outbox message's (ADR 0018), so a retry republishes the same id; a failed publish (a consumer failed) throws, and the
/// outbox retries.
/// </summary>
internal sealed class PublishExternalLoginUnlinkedDomainEventHandler(
    IIntegrationEventPublisher publisher,
    IOutboxMessageContext messageContext,
    IExternalProviderCatalog catalog) : IDomainEventHandler<ExternalLoginUnlinkedDomainEvent>
{
    public Task HandleAsync(ExternalLoginUnlinkedDomainEvent domainEvent, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            new ExternalLoginUnlinkedIntegrationEvent(
                messageContext.MessageId,
                messageContext.OccurredAt,
                domainEvent.UserId,
                domainEvent.Provider,
                catalog.DisplayNameOf(domainEvent.Provider)),
            cancellationToken);
}
