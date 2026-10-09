using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.Modules.Auth.Domain.Verification.Events;

namespace TemplateName.Modules.Auth.Application.Verification;

/// <summary>
/// Run by the outbox: decrypts the token of a newly issued code (ADR 0017) and emails its link, the confirmation email for
/// <see cref="VerificationPurpose.EmailVerify"/> and the reset email for <see cref="VerificationPurpose.PasswordReset"/>, in English
/// (decision D8). A user who no longer exists, or no longer has the address the code was issued for, gets nothing. A failed send throws,
/// so the outbox retries. Neither the token, the link nor the address is ever logged.
/// </summary>
internal sealed partial class SendVerificationEmailDomainEventHandler(
    IUserRepository users,
    ISecretProtector secretProtector,
    IEmailSender emailSender,
    IOptions<LinksOptions> linksOptions,
    ILogger<SendVerificationEmailDomainEventHandler> logger) : IDomainEventHandler<VerificationCodeIssuedDomainEvent>
{
    public async Task HandleAsync(VerificationCodeIssuedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(domainEvent.UserId, cancellationToken);
        if (user is null)
        {
            LogUserMissing(logger, domainEvent.CodeId);
            return;
        }

        if (!string.Equals(user.NormalizedEmail, domainEvent.Target, StringComparison.Ordinal))
        {
            LogAddressChanged(logger, domainEvent.CodeId);
            return;
        }

        var token = secretProtector.Unprotect(domainEvent.ProtectedToken);
        var links = linksOptions.Value;
        var message = domainEvent.Purpose switch
        {
            VerificationPurpose.EmailVerify => AuthEmails.ConfirmEmail(user.Email, user.DisplayName, links.ConfirmEmailLink(token)),
            VerificationPurpose.PasswordReset => AuthEmails.ResetPassword(user.Email, user.DisplayName, links.ResetPasswordLink(token)),
            _ => throw new InvalidOperationException($"No email is defined for the verification purpose {domainEvent.Purpose}."),
        };

        await emailSender.SendAsync(message, cancellationToken);
        LogSent(logger, domainEvent.Purpose, domainEvent.CodeId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent the {Purpose} email of verification code {CodeId}")]
    private static partial void LogSent(ILogger logger, VerificationPurpose purpose, Guid codeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped the email of verification code {CodeId}: its user no longer exists")]
    private static partial void LogUserMissing(ILogger logger, Guid codeId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped the email of verification code {CodeId}: its user's address has changed")]
    private static partial void LogAddressChanged(ILogger logger, Guid codeId);
}
