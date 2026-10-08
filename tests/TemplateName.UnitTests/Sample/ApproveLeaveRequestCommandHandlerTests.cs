using NSubstitute;
using TemplateName.Modules.Sample.Application.Abstractions;
using TemplateName.Modules.Sample.Application.LeaveRequests.Approve;
using TemplateName.Modules.Sample.Domain.LeaveRequests;

namespace TemplateName.UnitTests.Sample;

public sealed class ApproveLeaveRequestCommandHandlerTests
{
    private static readonly Guid ApproverId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly ILeaveRequestRepository _repository = Substitute.For<ILeaveRequestRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly ApproveLeaveRequestCommandHandler _sut;

    public ApproveLeaveRequestCommandHandlerTests()
    {
        _sut = new ApproveLeaveRequestCommandHandler(_repository, _unitOfWork);
    }

    [Fact]
    public async Task Unknown_id_returns_not_found()
    {
        var id = Guid.NewGuid();
        _repository.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((LeaveRequest?)null);

        var result = await _sut.HandleAsync(new ApproveLeaveRequestCommand(id, ApproverId), TestContext.Current.CancellationToken);

        result.Error.Code.ShouldBe("leave.not_found");
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Pending_request_is_approved_and_saved()
    {
        var leaveRequest = SubmitPending();
        _repository.GetByIdAsync(leaveRequest.Id, Arg.Any<CancellationToken>()).Returns(leaveRequest);

        var result = await _sut.HandleAsync(
            new ApproveLeaveRequestCommand(leaveRequest.Id, ApproverId),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        leaveRequest.Status.ShouldBe(LeaveRequestStatus.Approved);
        leaveRequest.ApproverId.ShouldBe(ApproverId);
        await _unitOfWork.Received(1).SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Already_approved_returns_not_pending_and_does_not_save()
    {
        var leaveRequest = SubmitPending();
        leaveRequest.Approve(ApproverId);
        _repository.GetByIdAsync(leaveRequest.Id, Arg.Any<CancellationToken>()).Returns(leaveRequest);

        var result = await _sut.HandleAsync(
            new ApproveLeaveRequestCommand(leaveRequest.Id, ApproverId),
            TestContext.Current.CancellationToken);

        result.Error.ShouldBe(LeaveRequestErrors.NotPending);
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private static LeaveRequest SubmitPending()
        => LeaveRequest.Submit(
            Guid.NewGuid(),
            new DateOnly(2026, 3, 2),
            new DateOnly(2026, 3, 4),
            "Trip",
            new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero)).Value;
}
