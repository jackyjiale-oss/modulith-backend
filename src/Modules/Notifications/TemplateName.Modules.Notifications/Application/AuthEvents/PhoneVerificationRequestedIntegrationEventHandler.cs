using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;

namespace TemplateName.Modules.Notifications.Application.AuthEvents;

/// <summary>
/// Consumes <see cref="PhoneVerificationRequestedIntegrationEvent"/> as <see cref="AuthNotificationTypes.PhoneVerification"/>: the code,
/// texted to the number it was issued for and expiring with it, so a text that waited past the code's life is never sent.
/// </summary>
internal sealed class PhoneVerificationRequestedIntegrationEventHandler(NotificationScheduler scheduler, TimeProvider timeProvider)
    : IIntegrationEventHandler<PhoneVerificationRequestedIntegrationEvent>
{
    public Task HandleAsync(PhoneVerificationRequestedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return scheduler.ScheduleAsync(
            AuthEventRequests.PhoneCode(
                AuthNotificationTypes.PhoneVerification,
                integrationEvent,
                integrationEvent.UserId,
                integrationEvent.PhoneNumber,
                integrationEvent.ProtectedCode,
                integrationEvent.ExpiresAt,
                timeProvider.GetUtcNow(),
                GetType()),
            contactVariables: null,
            cancellationToken);
    }
}
