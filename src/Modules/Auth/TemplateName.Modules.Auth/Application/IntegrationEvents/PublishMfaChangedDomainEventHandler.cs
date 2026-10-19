using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Domain.Users.Events;

namespace TemplateName.Modules.Auth.Application.IntegrationEvents;

/// <summary>
/// Run by the outbox: publishes <see cref="MfaChangedIntegrationEvent"/> when a user's two-step verification changes or a recovery code
/// is used, with the user's id and the kind of change only. The id and time are the outbox message's (ADR 0018), so a retry republishes
/// the same id; a failed publish (a consumer failed) throws, and the outbox retries. A change this handler does not know throws, so a
/// new <see cref="MfaChange"/> cannot be dropped silently.
/// </summary>
internal sealed class PublishMfaChangedDomainEventHandler(
    IIntegrationEventPublisher publisher,
    IOutboxMessageContext messageContext) : IDomainEventHandler<MfaChangedDomainEvent>
{
    public Task HandleAsync(MfaChangedDomainEvent domainEvent, CancellationToken cancellationToken)
        => publisher.PublishAsync(
            new MfaChangedIntegrationEvent(
                messageContext.MessageId,
                messageContext.OccurredAt,
                domainEvent.UserId,
                Map(domainEvent.Change)),
            cancellationToken);

    private static MfaChangeKind Map(MfaChange change) => change switch
    {
        MfaChange.Enabled => MfaChangeKind.Enabled,
        MfaChange.Disabled => MfaChangeKind.Disabled,
        MfaChange.RecoveryCodesRegenerated => MfaChangeKind.RecoveryCodesRegenerated,
        MfaChange.ResetByAdmin => MfaChangeKind.ResetByAdmin,
        MfaChange.RecoveryCodeUsed => MfaChangeKind.RecoveryCodeUsed,
        _ => throw new InvalidOperationException($"The MFA change {(int)change} has no integration event kind."),
    };
}
