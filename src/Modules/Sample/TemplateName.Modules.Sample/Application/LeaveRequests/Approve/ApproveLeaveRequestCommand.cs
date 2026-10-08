using TemplateName.Application.Common.Messaging;

namespace TemplateName.Modules.Sample.Application.LeaveRequests.Approve;

/// <summary>The approver comes from the request body until the Auth plan replaces it with <c>ICurrentUser</c>.</summary>
internal sealed record ApproveLeaveRequestCommand(Guid LeaveRequestId, Guid ApproverId) : ICommand;
