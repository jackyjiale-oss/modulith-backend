using Microsoft.AspNetCore.Mvc;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>
/// The query string of the role list, bound with <c>[AsParameters]</c>. Without the explicit names the OpenAPI document would list the
/// parameters in PascalCase; query parameters are camelCase (docs/coding-conventions.md Section 7).
/// </summary>
internal sealed record ListRolesRequest(
    [FromQuery(Name = "pageSize")] int? PageSize,
    [FromQuery(Name = "cursor")] string? Cursor,
    [FromQuery(Name = "sort")] string? Sort,
    [FromQuery(Name = "includeTotalCount")] bool? IncludeTotalCount);

/// <summary>The query string of the permission list; see <see cref="ListRolesRequest"/>.</summary>
internal sealed record ListPermissionsRequest(
    [FromQuery(Name = "includeDeprecated")] bool? IncludeDeprecated,
    [FromQuery(Name = "pageSize")] int? PageSize,
    [FromQuery(Name = "cursor")] string? Cursor,
    [FromQuery(Name = "sort")] string? Sort,
    [FromQuery(Name = "includeTotalCount")] bool? IncludeTotalCount);

/// <summary>The body of <c>POST admin/auth/roles</c> and <c>PUT admin/auth/roles/{id}</c>. The description is required but may be empty.</summary>
internal sealed record RoleRequest(string Name, string Description);

/// <summary>The body of <c>PUT admin/auth/roles/{id}/permissions</c>: the complete new set of permission ids (empty removes every grant).</summary>
internal sealed record SetRolePermissionsRequest(IReadOnlyCollection<Guid> PermissionIds);

/// <summary>The body of the <c>201</c> answer to <c>POST admin/auth/roles</c>.</summary>
internal sealed record CreateRoleResponse(Guid Id);
