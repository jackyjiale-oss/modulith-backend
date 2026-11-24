using Microsoft.Extensions.Logging;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Verification.Events;

namespace TemplateName.Modules.Auth.Application.IntegrationEvents;

/// <summary>
/// Publishes <see cref="PhoneSignInCodeRequestedIntegrationEvent"/> from the Auth outbox. The code is reloaded: one that is no longer
/// pending (a newer code ended it, it expired, it was used) or whose user is gone is not sent. The protected code is copied as it is;
/// Auth never decrypts it after issuing it.
/// </summary>
internal sealed partial class PublishPhoneSignInCodeIssuedDomainEventHandler(
    IPhoneVerificationCodeRepository codes,
    IUserRepository users,
    IIntegrationEventPublisher publisher,
    IOutboxMessageContext messageContext,
    TimeProvider timeProvider,
    ILogger<PublishPhoneSignInCodeIssuedDomainEventHandler> logger) : IDomainEventHandler<PhoneSignInCodeIssuedDomainEvent>
{
    public async Task HandleAsync(PhoneSignInCodeIssuedDomainEvent domainEvent, CancellationToken cancellationToken)
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
            new PhoneSignInCodeRequestedIntegrationEvent(
                messageContext.MessageId,
                messageContext.OccurredAt,
                code.UserId,
                code.PhoneNumber,
                domainEvent.ProtectedCode,
                code.ExpiresAt),
            cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped publishing phone sign-in code {CodeId}: it is unknown or no longer pending")]
    private static partial void LogCodeNotPending(ILogger logger, Guid codeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped publishing phone sign-in code {CodeId}: its user no longer exists")]
    private static partial void LogUserMissing(ILogger logger, Guid codeId);
}
