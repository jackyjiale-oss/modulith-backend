using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary>
/// Notification types for the delivery worker tests, registered by the integration test factory next to Auth's. Their templates are
/// embedded in this test assembly from <c>Notifications/Templates/{Email,InApp}/</c>, in <c>en</c> only (every culture falls back to it).
/// All are <see cref="NotificationPriority.Normal"/>, so quiet hours defer their email.
/// </summary>
internal sealed class DeliveryTestTypeSource : INotificationTypeSource
{
    /// <summary>An email with <c>display_name</c> in the subject and the body.</summary>
    public const string Email = "test.delivery_email";

    /// <summary>An in-app notice with <c>display_name</c> in the title and the body.</summary>
    public const string InApp = "test.delivery_in_app";

    /// <summary>An email whose templates do not exist, so rendering always fails.</summary>
    public const string MissingTemplate = "test.delivery_missing_template";

    public IReadOnlyCollection<NotificationTypeDefinition> Types { get; } =
    [
        Define(Email, NotificationChannel.Email),
        Define(InApp, NotificationChannel.InApp),
        Define(MissingTemplate, NotificationChannel.Email),
    ];

    private static NotificationTypeDefinition Define(string code, NotificationChannel channel) =>
        new(
            code,
            NotificationCategory.System,
            NotificationPriority.Normal,
            [channel],
            [],
            new HashSet<string>(["display_name"], StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
}
