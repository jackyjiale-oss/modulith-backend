using TemplateName.Modules.Sample.Domain.LeaveRequests;

namespace TemplateName.Modules.Sample.Application.Abstractions;

internal interface ILeaveRequestRepository
{
    Task<LeaveRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    void Add(LeaveRequest leaveRequest);
}
