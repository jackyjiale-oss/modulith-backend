using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;

namespace TemplateName.Modules.Notifications.Application.AuthEvents;

/// <summary>
/// Consumes <see cref="UserLockedOutIntegrationEvent"/> as <see cref="AuthNotificationTypes.AccountLocked"/>, with
/// <c>locked_until</c> (the lockout's end) in the recipient's local time.
/// </summary>
internal sealed class UserLockedOutIntegrationEventHandler(NotificationScheduler scheduler)
    : IIntegrationEventHandler<UserLockedOutIntegrationEvent>
{
    public Task HandleAsync(UserLockedOutIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return scheduler.ScheduleAsync(
            AuthEventRequests.Notice(AuthNotificationTypes.AccountLocked, integrationEvent, integrationEvent.UserId, GetType()),
            AuthEventRequests.LocalTime("locked_until", integrationEvent.LockoutEnd),
            cancellationToken);
    }
}
