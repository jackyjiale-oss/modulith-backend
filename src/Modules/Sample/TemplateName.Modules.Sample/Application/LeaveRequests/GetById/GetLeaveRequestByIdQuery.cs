using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Sample.Application.LeaveRequests.GetById;

internal sealed record GetLeaveRequestByIdQuery(Guid LeaveRequestId) : IQuery<LeaveRequestResponse>;
