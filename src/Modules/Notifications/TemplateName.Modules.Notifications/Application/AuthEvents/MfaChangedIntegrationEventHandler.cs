using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Contracts.IntegrationEvents;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Application.Scheduling;

namespace TemplateName.Modules.Notifications.Application.AuthEvents;

/// <summary>
/// Consumes <see cref="MfaChangedIntegrationEvent"/> as <see cref="AuthNotificationTypes.MfaChanged"/>, with <c>change</c> (the kind of
/// change as a fixed code the template switches on) and <c>occurred_at</c> in the recipient's local time. An unknown kind throws (a
/// programming error: a kind was added to the contract without a sentence for it).
/// </summary>
internal sealed class MfaChangedIntegrationEventHandler(NotificationScheduler scheduler)
    : IIntegrationEventHandler<MfaChangedIntegrationEvent>
{
    public Task HandleAsync(MfaChangedIntegrationEvent integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        var variables = new Dictionary<string, string> { ["change"] = CodeOf(integrationEvent.Change) };

        return scheduler.ScheduleAsync(
            AuthEventRequests.Notice(AuthNotificationTypes.MfaChanged, integrationEvent, integrationEvent.UserId, GetType(), variables),
            AuthEventRequests.LocalTime("occurred_at", integrationEvent.OccurredAt),
            cancellationToken);
    }

    private static string CodeOf(MfaChangeKind change) => change switch
    {
        MfaChangeKind.Enabled => "enabled",
        MfaChangeKind.Disabled => "disabled",
        MfaChangeKind.RecoveryCodesRegenerated => "recovery_codes_regenerated",
        MfaChangeKind.ResetByAdmin => "reset_by_admin",
        MfaChangeKind.RecoveryCodeUsed => "recovery_code_used",
        _ => throw new InvalidOperationException($"No notification wording is defined for the two-step verification change {change}."),
    };
}
