using Microsoft.Extensions.Logging;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Domain.Users.Events;

namespace TemplateName.Modules.Auth.Application.Verification;

/// <summary>
/// Run by the outbox: tells the owner of an account that someone tried to register with its address (no link, English only, decision
/// D8). This email is the only visible difference between registering a new and a known address, and only the owner sees it. A user
/// who no longer exists gets nothing; a failed send throws, so the outbox retries. The address is never logged.
/// </summary>
internal sealed partial class SendRegistrationAttemptedEmailDomainEventHandler(
    IUserRepository users,
    IEmailSender emailSender,
    ILogger<SendRegistrationAttemptedEmailDomainEventHandler> logger) : IDomainEventHandler<RegistrationAttemptedDomainEvent>
{
    public async Task HandleAsync(RegistrationAttemptedDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        var user = await users.GetByIdAsync(domainEvent.UserId, cancellationToken);
        if (user is null)
        {
            LogUserMissing(logger, domainEvent.UserId);
            return;
        }

        await emailSender.SendAsync(AuthEmails.RegistrationAttempted(user.Email, user.DisplayName), cancellationToken);
        LogSent(logger, domainEvent.UserId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent the registration-attempt notice to user {UserId}")]
    private static partial void LogSent(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Skipped the registration-attempt notice: user {UserId} no longer exists")]
    private static partial void LogUserMissing(ILogger logger, Guid userId);
}
