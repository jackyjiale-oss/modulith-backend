using TemplateName.Application.Common.Identity;

namespace TemplateName.Modules.Audit.Application;

/// <summary>
/// Declares the permissions of the Audit module so the Auth seeder can sync them into the permission table; this module never
/// references the Auth module. The permission is deliberately not <see cref="PermissionDefinition.AdminDefault"/>: its holder reads every
/// audited property of every module regardless of that module's own read permissions, so only <c>SuperAdmin</c> (which always holds every
/// permission) has it by default; granting it to another role is a deliberate choice.
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
            AdminDefault: false),
    ];

    public IReadOnlyCollection<PermissionDefinition> Permissions => All;
}
