using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Sample.Application.Abstractions;
using TemplateName.Modules.Sample.Domain.LeaveRequests;
using TemplateName.SharedKernel;

namespace TemplateName.Modules.Sample.Application.LeaveRequests.Submit;

internal sealed class SubmitLeaveRequestCommandHandler(
    ILeaveRequestRepository repository,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider) : ICommandHandler<SubmitLeaveRequestCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(SubmitLeaveRequestCommand command, CancellationToken cancellationToken)
    {
        var result = LeaveRequest.Submit(
            command.EmployeeId,
            command.StartDate,
            command.EndDate,
            command.Reason,
            timeProvider.GetUtcNow());
        if (result.IsFailure)
        {
            return Result.Failure<Guid>(result.Error);
        }

        repository.Add(result.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return result.Value.Id;
    }
}
