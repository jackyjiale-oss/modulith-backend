using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Sample.Application.Abstractions;
using TemplateName.Modules.Sample.Domain.LeaveRequests;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Sample.Application.LeaveRequests.Approve;

internal sealed class ApproveLeaveRequestCommandHandler(
    ILeaveRequestRepository repository,
    IUnitOfWork unitOfWork) : ICommandHandler<ApproveLeaveRequestCommand>
{
    public async Task<Result> HandleAsync(ApproveLeaveRequestCommand command, CancellationToken cancellationToken)
    {
        var leaveRequest = await repository.GetByIdAsync(command.LeaveRequestId, cancellationToken);
        if (leaveRequest is null)
        {
            return Result.Failure(LeaveRequestErrors.NotFound(command.LeaveRequestId));
        }

        var result = leaveRequest.Approve(command.ApproverId);
        if (result.IsFailure)
        {
            return result;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }
}
