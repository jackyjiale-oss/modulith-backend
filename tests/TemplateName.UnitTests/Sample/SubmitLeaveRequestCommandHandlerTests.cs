using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TemplateName.Modules.Sample.Application.Abstractions;
using TemplateName.Modules.Sample.Application.LeaveRequests.Submit;
using TemplateName.Modules.Sample.Domain.LeaveRequests;

namespace TemplateName.UnitTests.Sample;

public sealed class SubmitLeaveRequestCommandHandlerTests
{
    private readonly ILeaveRequestRepository _repository = Substitute.For<ILeaveRequestRepository>();
    private readonly IUnitOfWork _unitOfWork = Substitute.For<IUnitOfWork>();
    private readonly FakeTimeProvider _timeProvider = new(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
    private readonly SubmitLeaveRequestCommandHandler _sut;

    public SubmitLeaveRequestCommandHandlerTests()
    {
        _sut = new SubmitLeaveRequestCommandHandler(_repository, _unitOfWork, _timeProvider);
    }

    [Fact]
    public async Task Returns_new_id_adds_and_saves_once()
    {
        var employeeId = Guid.NewGuid();
        var command = new SubmitLeaveRequestCommand(employeeId, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 4), "Trip");

        var result = await _sut.HandleAsync(command, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        _repository.Received(1).Add(Arg.Is<LeaveRequest>(leaveRequest =>
            leaveRequest.Id == result.Value
            && leaveRequest.EmployeeId == employeeId
            && leaveRequest.Status == LeaveRequestStatus.Pending));
        await _unitOfWork.Received(1).SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Invalid_range_returns_error_and_does_not_save()
    {
        var command = new SubmitLeaveRequestCommand(Guid.NewGuid(), new DateOnly(2026, 3, 4), new DateOnly(2026, 3, 2), "Trip");

        var result = await _sut.HandleAsync(command, TestContext.Current.CancellationToken);

        result.Error.ShouldBe(LeaveRequestErrors.InvalidDateRange);
        _repository.DidNotReceive().Add(Arg.Any<LeaveRequest>());
        await _unitOfWork.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
