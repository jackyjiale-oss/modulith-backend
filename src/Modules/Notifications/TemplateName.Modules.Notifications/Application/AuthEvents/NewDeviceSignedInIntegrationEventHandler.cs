using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;

namespace TemplateName.Modules.Notifications.Application.AuthEvents;

/// <summary>
/// Consumes <see cref="NewDeviceSignedInIntegrationEvent"/> as <see cref="AuthNotificationTypes.NewDeviceLogin"/>, with
/// <c>occurred_at</c> in the recipient's local time and <c>ip_address</c>, the address the server observed (empty when it observed
/// none, which the template reads as "show no address"). The event carries no device name or user agent, so the email has none.
/// </summary>
internal sealed class NewDeviceSignedInIntegrationEventHandler(NotificationScheduler scheduler)
    : IIntegrationEventHandler<NewDeviceSignedInIntegrationEvent>
{
    public Task HandleAsync(NewDeviceSignedInIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var variables = new Dictionary<string, string> { ["ip_address"] = integrationEvent.IpAddress ?? string.Empty };

        return scheduler.ScheduleAsync(
            AuthEventRequests.Notice(AuthNotificationTypes.NewDeviceLogin, integrationEvent, integrationEvent.UserId, GetType(), variables),
            AuthEventRequests.LocalTime("occurred_at", integrationEvent.OccurredAt),
            cancellationToken);
    }
}
