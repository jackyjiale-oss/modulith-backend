using TemplateName.Application.Common.Identity;

namespace TemplateName.Modules.Notifications.Application;

/// <summary>
/// Declares the permissions of the Notifications module so the Auth seeder can sync them into the permission table; this module never
/// references the Auth module. Both are <see cref="PermissionDefinition.AdminDefault"/>: the <c>Admin</c> role gets them in the run
/// that first inserts them, <c>SuperAdmin</c> always holds them.
/// </summary>
internal sealed class NotificationsPermissionSource : IPermissionSource
{
    private const string ModuleName = "notifications";

    private static readonly PermissionDefinition[] All =
    [
        new(
            NotificationsPermissions.DeliveryView,
            ModuleName,
            "View deliveries",
            "List notification deliveries and their status, with the destination masked.",
            AdminDefault: true),
        new(
            NotificationsPermissions.DeliveryRetry,
            ModuleName,
            "Retry deliveries",
            "Put a dead-lettered notification delivery back in the queue.",
            AdminDefault: true),
    ];

    public IReadOnlyCollection<PermissionDefinition> Permissions => All;
}
