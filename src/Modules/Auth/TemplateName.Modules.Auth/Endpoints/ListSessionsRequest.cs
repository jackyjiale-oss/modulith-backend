using Microsoft.AspNetCore.Mvc;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>
/// The query string of the session list, bound with <c>[AsParameters]</c>. Without the explicit names the OpenAPI document would list
/// the parameters in PascalCase; query parameters are camelCase (docs/coding-conventions.md Section 7).
/// </summary>
internal sealed record ListSessionsRequest(
    [FromQuery(Name = "pageSize")] int? PageSize,
    [FromQuery(Name = "cursor")] string? Cursor,
    [FromQuery(Name = "sort")] string? Sort,
    [FromQuery(Name = "includeTotalCount")] bool? IncludeTotalCount);
