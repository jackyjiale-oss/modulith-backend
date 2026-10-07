using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Sample.Domain.LeaveRequests;

namespace TemplateName.Modules.Sample.Application.LeaveRequests.List;

/// <summary>A page of leave requests, optionally only one employee's and/or one status.</summary>
internal sealed record ListLeaveRequestsQuery(Guid? EmployeeId, LeaveRequestStatus? Status, CursorPageRequest Page)
    : IQuery<CursorPage<LeaveRequestListItemResponse>>;
