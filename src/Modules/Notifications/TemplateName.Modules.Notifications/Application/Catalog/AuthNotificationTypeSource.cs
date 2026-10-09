using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Application.Catalog;

/// <summary>
/// Declares the notification types of Auth's integration events (Decisions D6, D7, D10). Every one is a security notice: category
/// <see cref="NotificationCategory.Security"/>, priority <see cref="NotificationPriority.Critical"/> (so it ignores quiet hours) and a
/// mandatory email channel. Only <see cref="AuthNotificationTypes.PasswordChanged"/> and
/// <see cref="AuthNotificationTypes.TokenReuseDetected"/> also have an in-app channel, which the user can switch off. The four types that
/// carry a single-use link declare <c>action_url</c> as a secret variable, which only their email text and HTML may show. The templates
/// are in <c>Templates/</c> of this project.
/// </summary>
internal sealed class AuthNotificationTypeSource : INotificationTypeSource
{
    private const string DisplayName = "display_name";
    private const string ExpiresInMinutes = "expires_in_minutes";
    private const string OccurredAt = "occurred_at";
    private const string LockedUntil = "locked_until";
    private const string TimeZone = "time_zone";
    private const string ActionUrl = "action_url";

    private static readonly NotificationChannel[] EmailOnly = [NotificationChannel.Email];
    private static readonly NotificationChannel[] EmailAndInApp = [NotificationChannel.Email, NotificationChannel.InApp];

    public IReadOnlyCollection<NotificationTypeDefinition> Types { get; } =
    [
        Link(AuthNotificationTypes.EmailVerification),
        Link(AuthNotificationTypes.PasswordReset),
        Link(AuthNotificationTypes.PasswordResetRequired),
        Link(AuthNotificationTypes.AccountCreated),
        Notice(AuthNotificationTypes.RegistrationAttempted, EmailOnly, DisplayName),
        Notice(AuthNotificationTypes.PasswordChanged, EmailAndInApp, DisplayName, OccurredAt, TimeZone),
        Notice(AuthNotificationTypes.AccountLocked, EmailOnly, DisplayName, LockedUntil, TimeZone),
        Notice(AuthNotificationTypes.TokenReuseDetected, EmailAndInApp, DisplayName, OccurredAt, TimeZone),
    ];

    private static NotificationTypeDefinition Link(string code) =>
        Define(code, EmailOnly, [DisplayName, ExpiresInMinutes], [ActionUrl]);

    private static NotificationTypeDefinition Notice(string code, NotificationChannel[] channels, params string[] variables) =>
        Define(code, channels, variables, []);

    private static NotificationTypeDefinition Define(string code, NotificationChannel[] channels, string[] variables, string[] secretVariables) =>
        new(
            code,
            NotificationCategory.Security,
            NotificationPriority.Critical,
            channels,
            EmailOnly,
            new HashSet<string>(variables, StringComparer.Ordinal),
            new HashSet<string>(secretVariables, StringComparer.Ordinal));
}
