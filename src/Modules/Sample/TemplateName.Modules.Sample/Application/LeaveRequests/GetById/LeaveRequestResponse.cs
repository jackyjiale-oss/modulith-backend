using TemplateName.Modules.Sample.Domain.LeaveRequests;

namespace TemplateName.Modules.Sample.Application.LeaveRequests.GetById;

internal sealed record LeaveRequestResponse(
    Guid Id,
    Guid EmployeeId,
    DateOnly StartDate,
    DateOnly EndDate,
    string Reason,
    LeaveRequestStatus Status,
    Guid? ApproverId,
    DateTime CreatedAt);
