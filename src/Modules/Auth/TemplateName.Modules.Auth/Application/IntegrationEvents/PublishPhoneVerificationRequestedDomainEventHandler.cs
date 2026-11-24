using Microsoft.Extensions.Logging;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Verification.Events;

namespace TemplateName.Modules.Auth.Application.IntegrationEvents;

/// <summary>
/// Run by the outbox: publishes a newly issued phone code as <see cref="PhoneVerificationRequestedIntegrationEvent"/> for the module
/// that texts it. The protected code is copied from the domain event as it is; nothing is decrypted here.
/// <para>
/// Nothing is published, and one Information line naming only the code id is logged, when the code is no longer pending (a newer code
/// replaced it, it was used, ran out of tries or expired) or its user no longer exists: a stale code is never sent. The event's id and
/// time are the outbox message's (ADR 0018), so a retry republishes the same id. Neither the code nor the number is ever logged.
/// </para>
/// </summary>
internal sealed partial class PublishPhoneVerificationRequestedDomainEventHandler(
    IPhoneVerificationCodeRepository codes,
    IUserRepository users,
    IIntegrationEventPublisher publisher,
    IOutboxMessageContext messageContext,
    TimeProvider timeProvider,
    ILogger<PublishPhoneVerificationRequestedDomainEventHandler> logger) : IDomainEventHandler<PhoneVerificationRequestedDomainEvent>
{
    public async Task HandleAsync(PhoneVerificationRequestedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var code = await codes.GetByIdAsync(domainEvent.CodeId, cancellationToken);
        if (code is null || !code.IsPending(timeProvider.GetUtcNow()))
        {
            LogCodeNotPending(logger, domainEvent.CodeId);
            return;
        }

        if (await users.GetByIdAsync(code.UserId, cancellationToken) is null)
        {
            LogUserMissing(logger, code.Id);
            return;
        }

        await publisher.PublishAsync(
            new PhoneVerificationRequestedIntegrationEvent(
                messageContext.MessageId,
                messageContext.OccurredAt,
                code.UserId,
                code.PhoneNumber,
                domainEvent.ProtectedCode,
                code.ExpiresAt),
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped publishing phone verification code {CodeId}: it is unknown or no longer pending")]
    private static partial void LogCodeNotPending(ILogger logger, Guid codeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped publishing phone verification code {CodeId}: its user no longer exists")]
    private static partial void LogUserMissing(ILogger logger, Guid codeId);
}
