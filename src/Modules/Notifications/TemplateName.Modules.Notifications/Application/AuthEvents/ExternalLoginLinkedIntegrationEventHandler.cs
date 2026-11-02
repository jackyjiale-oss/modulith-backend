using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;

namespace TemplateName.Modules.Notifications.Application.AuthEvents;

/// <summary>
/// Consumes <see cref="ExternalLoginLinkedIntegrationEvent"/> as <see cref="AuthNotificationTypes.ExternalLoginLinked"/>, with
/// <c>provider</c> (the provider's display name) and <c>occurred_at</c> in the recipient's local time.
/// </summary>
internal sealed class ExternalLoginLinkedIntegrationEventHandler(NotificationScheduler scheduler)
    : IIntegrationEventHandler<ExternalLoginLinkedIntegrationEvent>
{
    public Task HandleAsync(ExternalLoginLinkedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var variables = new Dictionary<string, string> { ["provider"] = integrationEvent.ProviderDisplayName };

        return scheduler.ScheduleAsync(
            AuthEventRequests.Notice(AuthNotificationTypes.ExternalLoginLinked, integrationEvent, integrationEvent.UserId, GetType(), variables),
            AuthEventRequests.LocalTime("occurred_at", integrationEvent.OccurredAt),
            cancellationToken);
    }
}
