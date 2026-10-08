using Microsoft.EntityFrameworkCore;
using TemplateName.Modules.Sample.Application.Abstractions;
using TemplateName.Modules.Sample.Domain.LeaveRequests;

namespace TemplateName.Modules.Sample.Infrastructure.Persistence;

internal sealed class LeaveRequestRepository(SampleDbContext context) : ILeaveRequestRepository
{
    public Task<LeaveRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => context.Set<LeaveRequest>().SingleOrDefaultAsync(leaveRequest => leaveRequest.Id == id, cancellationToken);

    public void Add(LeaveRequest leaveRequest) => context.Set<LeaveRequest>().Add(leaveRequest);
}
