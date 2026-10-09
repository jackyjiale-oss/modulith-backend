using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;

namespace TemplateName.Modules.Notifications.Application.AuthEvents;

/// <summary>
/// Consumes <see cref="RefreshTokenReuseDetectedIntegrationEvent"/> as <see cref="AuthNotificationTypes.TokenReuseDetected"/>, with
/// <c>occurred_at</c> in the recipient's local time.
/// </summary>
internal sealed class RefreshTokenReuseDetectedIntegrationEventHandler(NotificationScheduler scheduler)
    : IIntegrationEventHandler<RefreshTokenReuseDetectedIntegrationEvent>
{
    public Task HandleAsync(RefreshTokenReuseDetectedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return scheduler.ScheduleAsync(
            AuthEventRequests.Notice(AuthNotificationTypes.TokenReuseDetected, integrationEvent, integrationEvent.UserId, GetType()),
            AuthEventRequests.LocalTime("occurred_at", integrationEvent.OccurredAt),
            cancellationToken);
    }
}
