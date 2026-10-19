using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;

namespace TemplateName.Modules.Notifications.Application.AuthEvents;

/// <summary>
/// Consumes <see cref="EmailVerificationRequestedIntegrationEvent"/> as <see cref="AuthNotificationTypes.EmailVerification"/>: the
/// confirmation link, emailed to the address it was issued for and expiring with it.
/// </summary>
internal sealed class EmailVerificationRequestedIntegrationEventHandler(NotificationScheduler scheduler, TimeProvider timeProvider)
    : IIntegrationEventHandler<EmailVerificationRequestedIntegrationEvent>
{
    public Task HandleAsync(EmailVerificationRequestedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return scheduler.ScheduleAsync(
            AuthEventRequests.Link(
                AuthNotificationTypes.EmailVerification,
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
}
