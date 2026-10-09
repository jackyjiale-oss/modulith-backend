using Microsoft.AspNetCore.Mvc;
using TemplateName.Modules.Auth.Domain.Users;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>
/// The query string of the user list, bound with <c>[AsParameters]</c>. Without the explicit names the OpenAPI document would list the
/// parameters in PascalCase; query parameters are camelCase (docs/coding-conventions.md Section 7).
/// </summary>
internal sealed record ListUsersRequest(
    [FromQuery(Name = "search")] string? Search,
    [FromQuery(Name = "status")] UserStatus? Status,
    [FromQuery(Name = "pageSize")] int? PageSize,
    [FromQuery(Name = "cursor")] string? Cursor,
    [FromQuery(Name = "sort")] string? Sort,
    [FromQuery(Name = "includeTotalCount")] bool? IncludeTotalCount);

/// <summary>
/// The body of <c>POST admin/auth/users</c>. <paramref name="Locale"/> defaults to the request's language; <paramref name="RoleIds"/>
/// null or empty gives the <c>User</c> role.
/// </summary>
internal sealed record CreateUserRequest(string Email, string DisplayName, string? Locale = null, IReadOnlyCollection<Guid>? RoleIds = null);

/// <summary>The body of <c>PUT admin/auth/users/{id}/roles</c>: the complete new set of role ids (empty removes every role).</summary>
internal sealed record AssignRolesRequest(IReadOnlyCollection<Guid> RoleIds);

/// <summary>The body of the <c>201</c> answer to <c>POST admin/auth/users</c>.</summary>
internal sealed record CreateUserResponse(Guid Id);
