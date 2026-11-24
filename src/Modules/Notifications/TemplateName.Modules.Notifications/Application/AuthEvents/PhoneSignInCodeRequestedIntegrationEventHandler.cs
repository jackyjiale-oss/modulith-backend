using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;

namespace TemplateName.Modules.Notifications.Application.AuthEvents;

/// <summary>
/// Texts a phone sign-in code (type <c>auth.phone_sign_in</c>, a mandatory SMS) to the account's verified number named in the event. The
/// code stays encrypted until the SMS channel renders it; the number and the code are never logged.
/// </summary>
internal sealed class PhoneSignInCodeRequestedIntegrationEventHandler(NotificationScheduler scheduler, TimeProvider timeProvider)
    : IIntegrationEventHandler<PhoneSignInCodeRequestedIntegrationEvent>
{
    public Task HandleAsync(PhoneSignInCodeRequestedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return scheduler.ScheduleAsync(
            AuthEventRequests.PhoneCode(
                AuthNotificationTypes.PhoneSignIn,
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
