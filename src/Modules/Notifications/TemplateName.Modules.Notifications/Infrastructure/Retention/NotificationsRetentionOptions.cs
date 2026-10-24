using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Notifications.Infrastructure.Retention;

/// <summary>
/// How long the Notifications module keeps its time-bound rows before a recurring cleanup job deletes them (section
/// <c>Notifications:Retention</c>, ADR 0028). A row goes only when it is strictly older than the retention; <see langword="null"/> or zero
/// turns that cleanup off and a negative value is refused when the host starts. The inbox of processed messages
/// (<c>InboxMessages</c>) is covered by <c>Retention:Inbox</c>, and user settings and preferences are not time-based.
/// </summary>
internal sealed class NotificationsRetentionOptions : IValidatableObject
{
    internal const string SectionName = "Notifications:Retention";

    /// <summary>
    /// How long a hub ticket is kept after its <c>ExpiresAt</c>. A ticket lives seconds, so its expiry covers a consumed ticket too; one
    /// that has not expired is never deleted.
    /// </summary>
    public TimeSpan? HubTickets { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How long a notification is kept after its <c>CreatedAt</c>; its deliveries, dead-lettered ones included, go with it. A notification
    /// that still has a pending delivery is kept whatever its age.
    /// </summary>
    public TimeSpan? Notifications { get; set; } = TimeSpan.FromDays(180);

    /// <summary>How long an inbox row is kept after its <c>CreatedAt</c>, read or unread, whatever became of its notification.</summary>
    public TimeSpan? InAppNotifications { get; set; } = TimeSpan.FromDays(180);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var (name, value) in new (string Name, TimeSpan? Value)[]
        {
            (nameof(HubTickets), HubTickets),
            (nameof(Notifications), Notifications),
            (nameof(InAppNotifications), InAppNotifications),
        })
        {
            if (value < TimeSpan.Zero)
            {
                yield return new ValidationResult($"{name} may not be negative; use zero to turn the cleanup off.", [name]);
            }
        }
    }
}
