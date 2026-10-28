namespace TemplateName.Modules.Audit.Application;

/// <summary>
/// The permission codes of the Audit module, <c>module.resource.action</c> in snake case. The administration routes require them by
/// constant.
/// </summary>
internal static class AuditPermissions
{
    /// <summary>List the audit trail (<c>GET admin/audit/trails</c>).</summary>
    public const string TrailsView = "audit.trails.view";
}
