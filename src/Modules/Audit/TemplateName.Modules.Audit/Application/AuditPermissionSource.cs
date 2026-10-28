using TemplateName.Application.Common.Identity;

namespace TemplateName.Modules.Audit.Application;

/// <summary>
/// Declares the permissions of the Audit module so the Auth seeder can sync them into the permission table; this module never
/// references the Auth module. The permission is <see cref="PermissionDefinition.AdminDefault"/>: the <c>Admin</c> role gets it in the
/// run that first inserts it, <c>SuperAdmin</c> always holds it.
/// </summary>
internal sealed class AuditPermissionSource : IPermissionSource
{
    private const string ModuleName = "audit";

    private static readonly PermissionDefinition[] All =
    [
        new(
            AuditPermissions.TrailsView,
            ModuleName,
            "View the audit trail",
            "List the recorded field changes of audited entities, with their old and new values.",
            AdminDefault: true),
    ];

    public IReadOnlyCollection<PermissionDefinition> Permissions => All;
}
