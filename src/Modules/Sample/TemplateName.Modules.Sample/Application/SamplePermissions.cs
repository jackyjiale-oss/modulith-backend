namespace TemplateName.Modules.Sample.Application;

/// <summary>The permission codes of the Sample module, <c>module.resource.action</c> in snake case. Endpoints require them by constant.</summary>
internal static class SamplePermissions
{
    public const string LeaveRequestView = "sample.leave_request.view";

    public const string LeaveRequestCreate = "sample.leave_request.create";

    public const string LeaveRequestApprove = "sample.leave_request.approve";
}
