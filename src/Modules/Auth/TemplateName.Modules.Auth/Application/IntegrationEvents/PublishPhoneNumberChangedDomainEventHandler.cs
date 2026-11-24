using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.Modules.Auth.Domain.Users.Events;

namespace TemplateName.Modules.Auth.Application.IntegrationEvents;

/// <summary>
/// Publishes <see cref="PhoneNumberChangedIntegrationEvent"/> from the Auth outbox, so the owner can be told that a number that signs
/// the account in was added, replaced or removed. The message id is the outbox message's, so a republish is the same message.
/// </summary>
internal sealed class PublishPhoneNumberChangedDomainEventHandler(
    IIntegrationEventPublisher publisher,
    IOutboxMessageContext messageContext) : IDomainEventHandler<PhoneNumberChangedDomainEvent>
{
    public Task HandleAsync(PhoneNumberChangedDomainEvent domainEvent, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            new PhoneNumberChangedIntegrationEvent(
                messageContext.MessageId,
                messageContext.OccurredAt,
                domainEvent.UserId,
                domainEvent.Change switch
                {
                    PhoneNumberChange.Added => PhoneNumberChangeKind.Added,
                    PhoneNumberChange.Replaced => PhoneNumberChangeKind.Replaced,
                    PhoneNumberChange.Removed => PhoneNumberChangeKind.Removed,
                    _ => throw new ArgumentOutOfRangeException(nameof(domainEvent), domainEvent.Change, "Unknown phone number change."),
                },
                domainEvent.LastDigits),
            cancellationToken);
}
