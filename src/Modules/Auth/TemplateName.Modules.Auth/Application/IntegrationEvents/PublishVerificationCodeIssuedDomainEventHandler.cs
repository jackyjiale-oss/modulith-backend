using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Security;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Domain.Verification.Events;

namespace TemplateName.Modules.Auth.Application.IntegrationEvents;

/// <summary>
/// Run by the outbox: publishes the link of a newly issued code as <see cref="EmailVerificationRequestedIntegrationEvent"/>
/// (<see cref="VerificationPurpose.EmailVerify"/>) or <see cref="PasswordResetRequestedIntegrationEvent"/>
/// (<see cref="VerificationPurpose.PasswordReset"/>, its reason taken from the event's <see cref="VerificationTrigger"/>), for the
/// module that sends it (ADR 0020). The token is decrypted (ADR 0017), the link built from <c>Auth:Links</c> and the link encrypted
/// again with the same <see cref="ISecretProtector"/>, so the event carries only ciphertext; the plaintext exists only in this method.
/// <para>
/// Nothing is published, and one Information line naming only the code id is logged, when the code is no longer pending (a newer code
/// replaced it, it was used, or it expired), its user no longer exists, or the user's address is no longer the one the code was issued
/// for: a stale link is never sent. The event's id and time are the outbox message's (ADR 0018), so a retry republishes the same id.
/// A failed publish (a consumer failed) throws, and the outbox retries. Neither the token, the link nor the address is ever logged.
/// </para>
/// </summary>
internal sealed partial class PublishVerificationCodeIssuedDomainEventHandler(
    IVerificationCodeRepository verificationCodes,
    IUserRepository users,
    ISecretProtector secretProtector,
    IIntegrationEventPublisher publisher,
    IOutboxMessageContext messageContext,
    IOptions<LinksOptions> linksOptions,
    TimeProvider timeProvider,
    ILogger<PublishVerificationCodeIssuedDomainEventHandler> logger) : IDomainEventHandler<VerificationCodeIssuedDomainEvent>
{
    public async Task HandleAsync(VerificationCodeIssuedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var code = await verificationCodes.GetByIdAsync(domainEvent.CodeId, cancellationToken);
        if (code is null || !code.IsPending(timeProvider.GetUtcNow()))
        {
            LogCodeNotPending(logger, domainEvent.CodeId);
            return;
        }

        var user = await users.GetByIdAsync(code.UserId, cancellationToken);
        if (user is null)
        {
            LogUserMissing(logger, code.Id);
            return;
        }

        if (!string.Equals(user.NormalizedEmail, code.Target, StringComparison.Ordinal))
        {
            LogAddressChanged(logger, code.Id);
            return;
        }

        var token = secretProtector.Unprotect(domainEvent.ProtectedToken);
        var links = linksOptions.Value;
        switch (code.Purpose)
        {
            case VerificationPurpose.EmailVerify:
                await publisher.PublishAsync(
                    new EmailVerificationRequestedIntegrationEvent(
                        messageContext.MessageId,
                        messageContext.OccurredAt,
                        user.Id,
                        user.Email,
                        secretProtector.Protect(links.ConfirmEmailLink(token)),
                        code.ExpiresAt),
                    cancellationToken);
                break;

            case VerificationPurpose.PasswordReset:
                await publisher.PublishAsync(
                    new PasswordResetRequestedIntegrationEvent(
                        messageContext.MessageId,
                        messageContext.OccurredAt,
                        user.Id,
                        user.Email,
                        secretProtector.Protect(links.ResetPasswordLink(token)),
                        code.ExpiresAt,
                        ReasonOf(domainEvent.Trigger)),
                    cancellationToken);
                break;

            default:
                throw new InvalidOperationException($"No integration event is defined for the verification purpose {code.Purpose}.");
        }

        LogPublished(logger, code.Purpose, code.Id);
    }

    private static PasswordResetReason ReasonOf(VerificationTrigger trigger) => trigger switch
    {
        VerificationTrigger.SelfService => PasswordResetReason.SelfService,
        VerificationTrigger.CreatedByAdmin => PasswordResetReason.CreatedByAdmin,
        VerificationTrigger.ForcedByAdmin => PasswordResetReason.ForcedByAdmin,
        _ => throw new InvalidOperationException($"No password reset reason is defined for the trigger {trigger}."),
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Published the {Purpose} link of verification code {CodeId}")]
    private static partial void LogPublished(ILogger logger, VerificationPurpose purpose, Guid codeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped publishing verification code {CodeId}: it is unknown or no longer pending")]
    private static partial void LogCodeNotPending(ILogger logger, Guid codeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped publishing verification code {CodeId}: its user no longer exists")]
    private static partial void LogUserMissing(ILogger logger, Guid codeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped publishing verification code {CodeId}: its user's address has changed")]
    private static partial void LogAddressChanged(ILogger logger, Guid codeId);
}
