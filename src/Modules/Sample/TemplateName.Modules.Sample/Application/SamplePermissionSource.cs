using TemplateName.Application.Common.Identity;

namespace TemplateName.Modules.Sample.Application;

/// <summary>
/// Declares the permissions of the Sample module. The Auth module's seeder syncs every registered permission source into its permission
/// table; this module never references the Auth module.
/// </summary>
internal sealed class SamplePermissionSource : IPermissionSource
{
    private const string ModuleName = "sample";

    private static readonly PermissionDefinition[] All =
    [
        Define(SamplePermissions.LeaveRequestView, "View leave requests", "List leave requests and read their details."),
        Define(SamplePermissions.LeaveRequestCreate, "Submit leave requests", "Submit a leave request."),
        Define(SamplePermissions.LeaveRequestApprove, "Approve leave requests", "Approve a pending leave request as the approver."),
    ];

    public IReadOnlyCollection<PermissionDefinition> Permissions => All;

    private static PermissionDefinition Define(string code, string name, string description) =>
        new(code, ModuleName, name, description);
}
