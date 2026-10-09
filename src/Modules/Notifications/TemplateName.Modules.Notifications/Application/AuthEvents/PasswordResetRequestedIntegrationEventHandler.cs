using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;

namespace TemplateName.Modules.Notifications.Application.AuthEvents;

/// <summary>
/// Consumes <see cref="PasswordResetRequestedIntegrationEvent"/> as the type of its reason: <see cref="AuthNotificationTypes.PasswordReset"/>
/// (the user asked), <see cref="AuthNotificationTypes.PasswordResetRequired"/> (an administrator forced it) or
/// <see cref="AuthNotificationTypes.AccountCreated"/> (an administrator created the account). The link is emailed to the address it
/// was issued for and the notification expires with it. An unknown reason throws (a programming error).
/// </summary>
internal sealed class PasswordResetRequestedIntegrationEventHandler(NotificationScheduler scheduler, TimeProvider timeProvider)
    : IIntegrationEventHandler<PasswordResetRequestedIntegrationEvent>
{
    public Task HandleAsync(PasswordResetRequestedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return scheduler.ScheduleAsync(
            AuthEventRequests.Link(
                TypeOf(integrationEvent.Reason),
                integrationEvent,
                integrationEvent.UserId,
                integrationEvent.Email,
                integrationEvent.ProtectedActionUrl,
                integrationEvent.ExpiresAt,
                timeProvider.GetUtcNow(),
                GetType()),
            contactVariables: null,
            cancellationToken);
    }

    private static string TypeOf(PasswordResetReason reason) => reason switch
    {
        PasswordResetReason.SelfService => AuthNotificationTypes.PasswordReset,
        PasswordResetReason.ForcedByAdmin => AuthNotificationTypes.PasswordResetRequired,
        PasswordResetReason.CreatedByAdmin => AuthNotificationTypes.AccountCreated,
        _ => throw new InvalidOperationException($"No notification type is defined for the password reset reason {reason}."),
    };
}
