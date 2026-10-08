using Microsoft.AspNetCore.Mvc;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>
/// The query string of the audit log list; see <see cref="ListRolesRequest"/>. <c>from</c> and <c>to</c> are text, read by
/// <c>AuditLogTimestamp</c> so that a value without an offset means UTC whatever the server's time zone is.
/// </summary>
internal sealed record ListAuditLogsRequest(
    [FromQuery(Name = "userId")] Guid? UserId,
    [FromQuery(Name = "eventType")] string? EventType,
    [FromQuery(Name = "succeeded")] bool? Succeeded,
    [FromQuery(Name = "from")] string? From,
    [FromQuery(Name = "to")] string? To,
    [FromQuery(Name = "pageSize")] int? PageSize,
    [FromQuery(Name = "cursor")] string? Cursor,
    [FromQuery(Name = "sort")] string? Sort,
    [FromQuery(Name = "includeTotalCount")] bool? IncludeTotalCount);
