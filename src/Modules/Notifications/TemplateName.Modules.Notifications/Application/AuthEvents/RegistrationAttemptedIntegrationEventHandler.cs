using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;

namespace TemplateName.Modules.Notifications.Application.AuthEvents;

/// <summary>
/// Consumes <see cref="RegistrationAttemptedIntegrationEvent"/> as <see cref="AuthNotificationTypes.RegistrationAttempted"/>, emailed to
/// the account owner's current address.
/// </summary>
internal sealed class RegistrationAttemptedIntegrationEventHandler(NotificationScheduler scheduler)
    : IIntegrationEventHandler<RegistrationAttemptedIntegrationEvent>
{
    public Task HandleAsync(RegistrationAttemptedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return scheduler.ScheduleAsync(
            AuthEventRequests.Notice(AuthNotificationTypes.RegistrationAttempted, integrationEvent, integrationEvent.UserId, GetType()),
            contactVariables: null,
            cancellationToken);
    }
}
