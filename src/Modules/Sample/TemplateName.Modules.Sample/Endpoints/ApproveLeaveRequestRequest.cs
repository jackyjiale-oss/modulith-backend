namespace TemplateName.Modules.Sample.Endpoints;

/// <summary>The approver comes from the body until the Auth plan replaces it with <c>ICurrentUser</c>.</summary>
internal sealed record ApproveLeaveRequestRequest(Guid ApproverId);
