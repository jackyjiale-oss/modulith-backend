using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Sample.Application.LeaveRequests.Submit;

internal sealed record SubmitLeaveRequestCommand(Guid EmployeeId, DateOnly StartDate, DateOnly EndDate, string Reason)
    : ICommand<Guid>;
