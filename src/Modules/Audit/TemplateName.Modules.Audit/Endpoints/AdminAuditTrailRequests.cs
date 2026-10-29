using Microsoft.AspNetCore.Mvc;

namespace TemplateName.Modules.Audit.Endpoints;

/// <summary>
/// The query string of the audit trail list. <c>from</c> and <c>to</c> are text, read by <c>AuditTrailTimestamp</c> so that a value
/// without an offset means UTC whatever the server's time zone is; <c>action</c> is text too, so a wrong name is a validation error and
/// not a binding failure.
/// </summary>
internal sealed record ListAuditTrailsRequest(
    [FromQuery(Name = "entityType")] string? EntityType,
    [FromQuery(Name = "entityId")] string? EntityId,
    [FromQuery(Name = "userId")] Guid? UserId,
    [FromQuery(Name = "changeSetId")] Guid? ChangeSetId,
    [FromQuery(Name = "module")] string? Module,
    [FromQuery(Name = "action")] string? Action,
    [FromQuery(Name = "from")] string? From,
    [FromQuery(Name = "to")] string? To,
    [FromQuery(Name = "pageSize")] int? PageSize,
    [FromQuery(Name = "cursor")] string? Cursor,
    [FromQuery(Name = "sort")] string? Sort,
    [FromQuery(Name = "includeTotalCount")] bool? IncludeTotalCount);
