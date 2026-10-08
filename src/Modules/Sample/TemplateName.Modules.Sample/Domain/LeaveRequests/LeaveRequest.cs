using TemplateName.Modules.Sample.Domain.LeaveRequests.Events;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Sample.Domain.LeaveRequests;

/// <summary>An employee's request for time off. It starts pending and is approved by another person.</summary>
internal sealed class LeaveRequest : AggregateRoot<Guid>, IAuditable, ISoftDeletable
{
    // EF Core materializes the aggregate through this constructor; callers use Submit.
    private LeaveRequest()
    {
    }

    public Guid EmployeeId { get; private set; }

    public DateOnly StartDate { get; private set; }

    public DateOnly EndDate { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public LeaveRequestStatus Status { get; private set; }

    public Guid? ApproverId { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public DateTime CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTime? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTime? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    public static Result<LeaveRequest> Submit(
        Guid employeeId,
        DateOnly startDate,
        DateOnly endDate,
        string reason,
        DateTimeOffset now)
    {
        if (endDate < startDate)
        {
            return LeaveRequestErrors.InvalidDateRange;
        }

        var leaveRequest = new LeaveRequest
        {
            Id = SequentialGuid.Create(now),
            EmployeeId = employeeId,
            StartDate = startDate,
            EndDate = endDate,
            Reason = reason,
            Status = LeaveRequestStatus.Pending,
        };
        leaveRequest.Raise(new LeaveRequestSubmittedDomainEvent(leaveRequest.Id, employeeId));

        return leaveRequest;
    }

    public Result Approve(Guid approverId)
    {
        if (Status != LeaveRequestStatus.Pending)
        {
            return Result.Failure(LeaveRequestErrors.NotPending);
        }

        Status = LeaveRequestStatus.Approved;
        ApproverId = approverId;
        Raise(new LeaveRequestApprovedDomainEvent(Id, approverId));

        return Result.Success();
    }
}
