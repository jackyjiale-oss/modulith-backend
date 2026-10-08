using Microsoft.AspNetCore.Mvc;
using TemplateName.Modules.Sample.Domain.LeaveRequests;

namespace TemplateName.Modules.Sample.Endpoints;

/// <summary>
/// The query string of the leave request list, bound with <c>[AsParameters]</c>. Without the explicit names the OpenAPI document
/// would list the parameters in PascalCase; query parameters are camelCase (docs/coding-conventions.md Section 7).
/// </summary>
internal sealed record ListLeaveRequestsRequest(
    [FromQuery(Name = "employeeId")] Guid? EmployeeId,
    [FromQuery(Name = "status")] LeaveRequestStatus? Status,
    [FromQuery(Name = "pageSize")] int? PageSize,
    [FromQuery(Name = "cursor")] string? Cursor,
    [FromQuery(Name = "sort")] string? Sort,
    [FromQuery(Name = "includeTotalCount")] bool? IncludeTotalCount);
