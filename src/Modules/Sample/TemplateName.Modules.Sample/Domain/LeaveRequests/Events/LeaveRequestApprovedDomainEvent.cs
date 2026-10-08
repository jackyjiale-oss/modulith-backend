using TemplateName.SharedKernel;

namespace TemplateName.Modules.Sample.Domain.LeaveRequests.Events;

internal sealed record LeaveRequestApprovedDomainEvent(Guid LeaveRequestId, Guid ApproverId) : IDomainEvent;
