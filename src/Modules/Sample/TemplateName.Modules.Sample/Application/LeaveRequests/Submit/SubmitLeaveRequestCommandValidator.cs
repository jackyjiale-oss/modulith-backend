using FluentValidation;

namespace TemplateName.Modules.Sample.Application.LeaveRequests.Submit;

internal sealed class SubmitLeaveRequestCommandValidator : AbstractValidator<SubmitLeaveRequestCommand>
{
    private const int MaxReasonLength = 500;

    public SubmitLeaveRequestCommandValidator()
    {
        RuleFor(command => command.EmployeeId).NotEmpty();
        RuleFor(command => command.Reason).NotEmpty().MaximumLength(MaxReasonLength);
        RuleFor(command => command.EndDate).GreaterThanOrEqualTo(command => command.StartDate);
    }
}
