using TemplateName.SharedKernel;

namespace TemplateName.Modules.Sample.Domain.LeaveRequests.Events;

internal sealed record LeaveRequestSubmittedDomainEvent(Guid LeaveRequestId, Guid EmployeeId) : IDomainEvent;
