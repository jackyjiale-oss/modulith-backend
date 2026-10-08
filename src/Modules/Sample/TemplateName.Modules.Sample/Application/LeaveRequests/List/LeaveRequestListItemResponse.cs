using TemplateName.Modules.Sample.Domain.LeaveRequests;

namespace TemplateName.Modules.Sample.Application.LeaveRequests.List;

/// <summary>A leave request as a list shows it: without the reason and approver, which the single-request read returns.</summary>
internal sealed record LeaveRequestListItemResponse(
    Guid Id,
    Guid EmployeeId,
    DateOnly StartDate,
    DateOnly EndDate,
    LeaveRequestStatus Status,
    DateTime CreatedAt);
