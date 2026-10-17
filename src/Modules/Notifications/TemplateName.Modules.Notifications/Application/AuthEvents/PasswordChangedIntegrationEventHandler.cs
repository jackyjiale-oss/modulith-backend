using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;

namespace TemplateName.Modules.Notifications.Application.AuthEvents;

/// <summary>
/// Consumes <see cref="PasswordChangedIntegrationEvent"/> as <see cref="AuthNotificationTypes.PasswordChanged"/>, with
/// <c>occurred_at</c> in the recipient's local time.
/// </summary>
internal sealed class PasswordChangedIntegrationEventHandler(NotificationScheduler scheduler)
    : IIntegrationEventHandler<PasswordChangedIntegrationEvent>
{
    public Task HandleAsync(PasswordChangedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return scheduler.ScheduleAsync(
            AuthEventRequests.Notice(AuthNotificationTypes.PasswordChanged, integrationEvent, integrationEvent.UserId, GetType()),
            AuthEventRequests.LocalTime("occurred_at", integrationEvent.OccurredAt),
            cancellationToken);
    }
}
