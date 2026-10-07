using TemplateName.Modules.Sample.Domain.LeaveRequests;
using TemplateName.Modules.Sample.Domain.LeaveRequests.Events;
using TemplateName.SharedKernel;

namespace TemplateName.UnitTests.Sample;

public sealed class LeaveRequestTests
{
    private static readonly Guid EmployeeId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ApproverId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Submit_valid_creates_pending_request_and_raises_event()
    {
        var result = LeaveRequest.Submit(EmployeeId, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 4), "Trip", Now);

        var leaveRequest = result.Value;
        leaveRequest.Status.ShouldBe(LeaveRequestStatus.Pending);
        leaveRequest.EmployeeId.ShouldBe(EmployeeId);
        leaveRequest.StartDate.ShouldBe(new DateOnly(2026, 3, 2));
        leaveRequest.EndDate.ShouldBe(new DateOnly(2026, 3, 4));
        leaveRequest.Reason.ShouldBe("Trip");
        leaveRequest.ApproverId.ShouldBeNull();
        leaveRequest.Id.ShouldNotBe(Guid.Empty);
        leaveRequest.DomainEvents.Single().ShouldBe(new LeaveRequestSubmittedDomainEvent(leaveRequest.Id, EmployeeId));
    }

    [Fact]
    public void Submit_single_day_range_is_valid()
    {
        var result = LeaveRequest.Submit(EmployeeId, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 2), "Appointment", Now);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Submit_end_before_start_fails()
    {
        var result = LeaveRequest.Submit(EmployeeId, new DateOnly(2026, 3, 4), new DateOnly(2026, 3, 2), "x", Now);

        result.Error.ShouldBe(LeaveRequestErrors.InvalidDateRange);
    }

    [Fact]
    public void Approve_pending_sets_approved_and_raises_event()
    {
        var leaveRequest = SubmitPending();

        var result = leaveRequest.Approve(ApproverId);

        result.IsSuccess.ShouldBeTrue();
        leaveRequest.Status.ShouldBe(LeaveRequestStatus.Approved);
        leaveRequest.ApproverId.ShouldBe(ApproverId);
        leaveRequest.DomainEvents.Last().ShouldBe(new LeaveRequestApprovedDomainEvent(leaveRequest.Id, ApproverId));
    }

    [Fact]
    public void Approve_twice_fails_with_not_pending()
    {
        var leaveRequest = SubmitPending();
        leaveRequest.Approve(ApproverId);

        var result = leaveRequest.Approve(ApproverId);

        result.Error.ShouldBe(LeaveRequestErrors.NotPending);
        leaveRequest.DomainEvents.OfType<LeaveRequestApprovedDomainEvent>().Count().ShouldBe(1);
    }

    [Fact]
    public void Error_codes_and_types_match_the_contract()
    {
        LeaveRequestErrors.InvalidDateRange.Code.ShouldBe("leave.invalid_date_range");
        LeaveRequestErrors.InvalidDateRange.Type.ShouldBe(ErrorType.Validation);
        LeaveRequestErrors.NotPending.Code.ShouldBe("leave.not_pending");
        LeaveRequestErrors.NotPending.Type.ShouldBe(ErrorType.Conflict);

        var notFound = LeaveRequestErrors.NotFound(EmployeeId);

        notFound.Code.ShouldBe("leave.not_found");
        notFound.Type.ShouldBe(ErrorType.NotFound);
        notFound.Parameters.ShouldNotBeNull()["id"].ShouldBe(EmployeeId);
    }

    private static LeaveRequest SubmitPending()
        => LeaveRequest.Submit(EmployeeId, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 4), "Trip", Now).Value;
}
