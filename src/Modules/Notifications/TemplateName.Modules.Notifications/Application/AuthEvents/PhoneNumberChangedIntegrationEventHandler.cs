using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;

namespace TemplateName.Modules.Notifications.Application.AuthEvents;

/// <summary>
/// Tells the owner that the verified phone number, which can sign the account in, was added, replaced or removed (type
/// <c>auth.phone_number_changed</c>, a mandatory email). It names the last three digits only, never the number.
/// </summary>
internal sealed class PhoneNumberChangedIntegrationEventHandler(NotificationScheduler scheduler)
    : IIntegrationEventHandler<PhoneNumberChangedIntegrationEvent>
{
    public Task HandleAsync(PhoneNumberChangedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var variables = new Dictionary<string, string>
        {
            ["kind"] = KindOf(integrationEvent.Kind),
            ["last_digits"] = integrationEvent.LastDigits,
        };

        return scheduler.ScheduleAsync(
            AuthEventRequests.Notice(AuthNotificationTypes.PhoneNumberChanged, integrationEvent, integrationEvent.UserId, GetType(), variables),
            AuthEventRequests.LocalTime("occurred_at", integrationEvent.OccurredAt),
            cancellationToken);
    }

    private static string KindOf(PhoneNumberChangeKind kind) => kind switch
    {
        PhoneNumberChangeKind.Added => "added",
        PhoneNumberChangeKind.Replaced => "replaced",
        PhoneNumberChangeKind.Removed => "removed",
        _ => throw new InvalidOperationException($"No notification wording is defined for the phone number change {kind}."),
    };
}
