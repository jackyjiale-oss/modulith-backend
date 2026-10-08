namespace TemplateName.Modules.Sample.Domain.LeaveRequests;

/// <summary>Where a leave request is in its life cycle. The numeric values are persisted, so they never change or get reused.</summary>
internal enum LeaveRequestStatus : byte
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Canceled = 4,
}
