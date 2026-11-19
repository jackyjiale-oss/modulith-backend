using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using TemplateName.Application.Common.Concurrency;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Auth.Application.Admin.Roles;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Application.Admin.Permissions.List;
using TemplateName.Modules.Auth.Application.Admin.Roles.Create;
using TemplateName.Modules.Auth.Application.Admin.Roles.Delete;
using TemplateName.Modules.Auth.Application.Admin.Roles.Get;
using TemplateName.Modules.Auth.Application.Admin.Roles.List;
using TemplateName.Modules.Auth.Application.Admin.Roles.SetPermissions;
using TemplateName.Modules.Auth.Application.Admin.Roles.Update;
using TemplateName.Web.Common.Concurrency;
using TemplateName.Web.Common.Results;
using TemplateName.Web.Common.Security;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>
/// Maps the role administration routes <c>admin/auth/roles/...</c> and the permission list <c>admin/auth/permissions</c> under the
/// host's <c>/api/v1</c> group.
/// </summary>
internal static class AdminRoleEndpoints
{
    private const string GetByIdRouteName = "GetAdminRole";

    internal static IEndpointRouteBuilder MapAdminRoleEndpoints(this IEndpointRouteBuilder app)
    {
        // Every route needs a signed-in caller with its permission (R13): 401 without a valid token, 403 without the permission.
        var roles = app.MapGroup("admin/auth/roles")
            .WithTags("Auth administration")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        roles.MapGet("/", ListAsync)
            .WithName("ListRoles")
            .RequirePermission(AuthPermissions.RoleView)
            .Produces<CursorPage<RoleListItemResponse>>()
            .ProducesValidationProblem();

        roles.MapGet("/{id:guid}", GetByIdAsync)
            .WithName(GetByIdRouteName)
            .RequirePermission(AuthPermissions.RoleView)
            .Produces<RoleResponse>()
            .ProducesEntityTag()
            .ProducesProblem(StatusCodes.Status404NotFound);

        roles.MapPost("/", CreateAsync)
            .WithName("CreateRole")
            .RequirePermission(AuthPermissions.RoleManage)
            .Produces<CreateRoleResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        roles.MapPut("/{id:guid}", UpdateAsync)
            .WithName("UpdateRole")
            .RequirePermission(AuthPermissions.RoleManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesEntityTag()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);

        roles.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteRole")
            .RequirePermission(AuthPermissions.RoleManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        roles.MapPut("/{id:guid}/permissions", SetPermissionsAsync)
            .WithName("SetRolePermissions")
            .RequirePermission(AuthPermissions.RoleManage)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesEntityTag()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status412PreconditionFailed);

        var permissions = app.MapGroup("admin/auth/permissions")
            .WithTags("Auth administration")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        permissions.MapGet("/", ListPermissionsAsync)
            .WithName("ListPermissions")
            .RequirePermission(AuthPermissions.PermissionView)
            .Produces<CursorPage<PermissionResponse>>()
            .ProducesValidationProblem();

        return app;
    }

    // By name by default (sort name or createdAt, ascending or with a leading -); soft-deleted roles are never listed.
    private static async Task<IResult> ListAsync(
        [AsParameters] ListRolesRequest request,
        IQueryHandler<ListRolesQuery, CursorPage<RoleListItemResponse>> handler,
        CancellationToken cancellationToken)
    {
        var page = new CursorPageRequest(
            request.PageSize ?? CursorPageRequest.DefaultPageSize,
            request.Cursor,
            request.Sort,
            request.IncludeTotalCount ?? false);
        var result = await handler.HandleAsync(new ListRolesQuery(page), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    // 404 auth.role_not_found for an unknown and a soft-deleted id alike.
    private static async Task<IResult> GetByIdAsync(Guid id, IQueryHandler<GetRoleQuery, RoleResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetRoleQuery(id), cancellationToken);
        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        var role = result.Value;
        return TypedResults.Ok(role).WithEntityTag(RoleVersion.Of(role.Name, role.Description, role.Permissions.Select(permission => permission.Id)));
    }

    // 201 with the new id and its Location; 409 auth.role_name_taken.
    private static async Task<IResult> CreateAsync(
        RoleRequest request,
        ICurrentUser currentUser,
        ICommandHandler<CreateRoleCommand, Guid> handler,
        LinkGenerator links,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } actorId)
        {
            return TypedResults.Unauthorized();
        }

        var result = await handler.HandleAsync(new CreateRoleCommand(actorId, request.Name, request.Description), cancellationToken);
        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        // A path (/api/v1/admin/auth/roles/{id}), not an absolute URI, so it holds behind proxies.
        var location = links.GetPathByName(GetByIdRouteName, new { id = result.Value });
        return TypedResults.Created(location, new CreateRoleResponse(result.Value));
    }

    // 204 with the new ETag; 400 request.malformed (If-Match); 404 auth.role_not_found; 409 auth.role_name_taken, 409
    // auth.system_role_protected; 412 concurrency.precondition_failed.
    private static async Task<IResult> UpdateAsync(
        Guid id,
        RoleRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ICurrentUser currentUser,
        ICommandHandler<UpdateRoleCommand, EntityTag> handler,
        CancellationToken cancellationToken)
    {
        if (!IfMatchHeader.TryParse(ifMatch, out var precondition))
        {
            return IfMatchHeader.Malformed();
        }

        return await AdminActor.RunAsync(
            currentUser,
            actorId => handler.HandleAsync(
                new UpdateRoleCommand(actorId, id, request.Name, request.Description) { IfMatch = precondition }, cancellationToken));
    }

    // 204 (a soft delete); 404 auth.role_not_found; 409 auth.system_role_protected.
    private static Task<IResult> DeleteAsync(Guid id, ICurrentUser currentUser, ICommandHandler<DeleteRoleCommand> handler, CancellationToken cancellationToken)
        => AdminActor.RunAsync(currentUser, actorId => handler.HandleAsync(new DeleteRoleCommand(actorId, id), cancellationToken), TypedResults.NoContent());

    // 204 with the new ETag; 400 auth.permission_not_found, 400 request.malformed (If-Match); 403 auth.permission_grant_not_allowed;
    // 404 auth.role_not_found; 409 auth.system_role_protected; 412 concurrency.precondition_failed.
    private static async Task<IResult> SetPermissionsAsync(
        Guid id,
        SetRolePermissionsRequest request,
        [FromHeader(Name = "If-Match")] string? ifMatch,
        ICurrentUser currentUser,
        ICommandHandler<SetRolePermissionsCommand, EntityTag> handler,
        CancellationToken cancellationToken)
    {
        if (!IfMatchHeader.TryParse(ifMatch, out var precondition))
        {
            return IfMatchHeader.Malformed();
        }

        return await AdminActor.RunAsync(
            currentUser,
            actorId => handler.HandleAsync(
                new SetRolePermissionsCommand(actorId, id, request.PermissionIds) { IfMatch = precondition }, cancellationToken));
    }

    // Sorted by code (the only sort, also descending with a leading -); permissions that no module declares any more only with
    // includeDeprecated=true.
    private static async Task<IResult> ListPermissionsAsync(
        [AsParameters] ListPermissionsRequest request,
        IQueryHandler<ListPermissionsQuery, CursorPage<PermissionResponse>> handler,
        CancellationToken cancellationToken)
    {
        var page = new CursorPageRequest(
            request.PageSize ?? CursorPageRequest.DefaultPageSize,
            request.Cursor,
            request.Sort,
            request.IncludeTotalCount ?? false);
        var result = await handler.HandleAsync(new ListPermissionsQuery(request.IncludeDeprecated ?? false, page), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }
}
