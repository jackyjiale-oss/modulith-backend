using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;

namespace TemplateName.Modules.Notifications.Application.AuthEvents;

/// <summary>
/// Consumes <see cref="ExternalLoginUnlinkedIntegrationEvent"/> as <see cref="AuthNotificationTypes.ExternalLoginUnlinked"/>, with
/// <c>provider</c> (the provider's display name) and <c>occurred_at</c> in the recipient's local time.
/// </summary>
internal sealed class ExternalLoginUnlinkedIntegrationEventHandler(NotificationScheduler scheduler)
    : IIntegrationEventHandler<ExternalLoginUnlinkedIntegrationEvent>
{
    public Task HandleAsync(ExternalLoginUnlinkedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var variables = new Dictionary<string, string> { ["provider"] = integrationEvent.ProviderDisplayName };

        return scheduler.ScheduleAsync(
            AuthEventRequests.Notice(AuthNotificationTypes.ExternalLoginUnlinked, integrationEvent, integrationEvent.UserId, GetType(), variables),
            AuthEventRequests.LocalTime("occurred_at", integrationEvent.OccurredAt),
            cancellationToken);
    }
}
