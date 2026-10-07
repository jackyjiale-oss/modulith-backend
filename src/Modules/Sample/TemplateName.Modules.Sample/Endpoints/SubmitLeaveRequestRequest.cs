namespace TemplateName.Modules.Sample.Endpoints;

internal sealed record SubmitLeaveRequestRequest(Guid EmployeeId, DateOnly StartDate, DateOnly EndDate, string Reason);
