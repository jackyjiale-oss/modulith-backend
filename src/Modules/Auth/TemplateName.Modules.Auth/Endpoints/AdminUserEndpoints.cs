using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Application.Admin.Users.AssignRoles;
using TemplateName.Modules.Auth.Application.Admin.Users.Create;
using TemplateName.Modules.Auth.Application.Admin.Users.ForcePasswordReset;
using TemplateName.Modules.Auth.Application.Admin.Users.Get;
using TemplateName.Modules.Auth.Application.Admin.Users.List;
using TemplateName.Modules.Auth.Application.Admin.Users.Lock;
using TemplateName.Modules.Auth.Application.Admin.Users.RevokeSessions;
using TemplateName.Modules.Auth.Application.Admin.Users.Unlock;
using TemplateName.SharedKernel;
using TemplateName.Web.Common.Results;
using TemplateName.Web.Common.Security;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>Maps the user administration routes <c>admin/auth/users/...</c> under the host's <c>/api/v1</c> group.</summary>
internal static class AdminUserEndpoints
{
    private const string GetByIdRouteName = "GetAdminUser";

    internal static IEndpointRouteBuilder MapAdminUserEndpoints(this IEndpointRouteBuilder app)
    {
        // Every route needs a signed-in caller with its permission (R13): 401 without a valid token, 403 without the permission.
        var group = app.MapGroup("admin/auth/users")
            .WithTags("Auth administration")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/", ListAsync)
            .WithName("ListUsers")
            .RequirePermission(AuthPermissions.UserView)
            .Produces<CursorPage<UserListItemResponse>>()
            .ProducesValidationProblem();

        group.MapGet("/{id:guid}", GetByIdAsync)
            .WithName(GetByIdRouteName)
            .RequirePermission(AuthPermissions.UserView)
            .Produces<UserResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateAsync)
            .WithName("CreateUser")
            .RequirePermission(AuthPermissions.UserCreate)
            .Produces<CreateUserResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/lock", LockAsync)
            .WithName("LockUser")
            .RequirePermission(AuthPermissions.UserLock)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("/{id:guid}/unlock", UnlockAsync)
            .WithName("UnlockUser")
            .RequirePermission(AuthPermissions.UserLock)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/force-password-reset", ForcePasswordResetAsync)
            .WithName("ForceUserPasswordReset")
            .RequirePermission(AuthPermissions.UserResetPassword)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/revoke-sessions", RevokeSessionsAsync)
            .WithName("RevokeUserSessions")
            .RequirePermission(AuthPermissions.UserRevokeSessions)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPut("/{id:guid}/roles", AssignRolesAsync)
            .WithName("AssignUserRoles")
            .RequirePermission(AuthPermissions.UserAssignRoles)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    // Newest first by default (sort createdAt or email, ascending or with a leading -); search is an email prefix, status Active or
    // Suspended.
    private static async Task<IResult> ListAsync(
        [AsParameters] ListUsersRequest request,
        IQueryHandler<ListUsersQuery, CursorPage<UserListItemResponse>> handler,
        CancellationToken cancellationToken)
    {
        var page = new CursorPageRequest(
            request.PageSize ?? CursorPageRequest.DefaultPageSize,
            request.Cursor,
            request.Sort,
            request.IncludeTotalCount ?? false);
        var result = await handler.HandleAsync(new ListUsersQuery(request.Search, request.Status, page), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    // 404 auth.user_not_found for an unknown and a soft-deleted id alike.
    private static async Task<IResult> GetByIdAsync(
        Guid id,
        IQueryHandler<GetUserQuery, UserResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetUserQuery(id), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    // 201 with the new id and its Location; the user sets a password through the emailed reset link. 409 auth.email_taken.
    private static async Task<IResult> CreateAsync(
        CreateUserRequest request,
        ICurrentUser currentUser,
        ICommandHandler<CreateUserCommand, Guid> handler,
        LinkGenerator links,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } actorId)
        {
            return TypedResults.Unauthorized();
        }

        var command = new CreateUserCommand(
            actorId,
            request.Email,
            request.DisplayName,
            request.Locale ?? CultureInfo.CurrentUICulture.Name,
            request.RoleIds);
        var result = await handler.HandleAsync(command, cancellationToken);
        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        // A path (/api/v1/admin/auth/users/{id}), not an absolute URI, so it holds behind proxies.
        var location = links.GetPathByName(GetByIdRouteName, new { id = result.Value });
        return TypedResults.Created(location, new CreateUserResponse(result.Value));
    }

    // 204, also for an account that is already locked; 400 auth.cannot_lock_self; 409 auth.last_super_admin.
    private static Task<IResult> LockAsync(Guid id, ICurrentUser currentUser, ICommandHandler<LockUserCommand> handler, CancellationToken cancellationToken)
        => SendAsync(currentUser, actorId => handler.HandleAsync(new LockUserCommand(actorId, id), cancellationToken), TypedResults.NoContent());

    private static Task<IResult> UnlockAsync(Guid id, ICurrentUser currentUser, ICommandHandler<UnlockUserCommand> handler, CancellationToken cancellationToken)
        => SendAsync(currentUser, actorId => handler.HandleAsync(new UnlockUserCommand(actorId, id), cancellationToken), TypedResults.NoContent());

    // 202 with no body: the link goes out through the outbox. 403 auth.account_inactive for a suspended account.
    private static Task<IResult> ForcePasswordResetAsync(
        Guid id,
        ICurrentUser currentUser,
        ICommandHandler<ForcePasswordResetCommand> handler,
        CancellationToken cancellationToken)
        => SendAsync(
            currentUser,
            actorId => handler.HandleAsync(new ForcePasswordResetCommand(actorId, id), cancellationToken),
            TypedResults.Accepted((string?)null));

    private static Task<IResult> RevokeSessionsAsync(
        Guid id,
        ICurrentUser currentUser,
        ICommandHandler<RevokeUserSessionsCommand> handler,
        CancellationToken cancellationToken)
        => SendAsync(currentUser, actorId => handler.HandleAsync(new RevokeUserSessionsCommand(actorId, id), cancellationToken), TypedResults.NoContent());

    // 204; 404 auth.user_not_found or auth.role_not_found; 409 auth.last_super_admin.
    private static Task<IResult> AssignRolesAsync(
        Guid id,
        AssignRolesRequest request,
        ICurrentUser currentUser,
        ICommandHandler<AssignRolesCommand> handler,
        CancellationToken cancellationToken)
        => SendAsync(
            currentUser,
            actorId => handler.HandleAsync(new AssignRolesCommand(actorId, id, request.RoleIds), cancellationToken),
            TypedResults.NoContent());

    // Every action names the acting administrator. The permission policy only lets a signed-in user with an id through; without one,
    // fail closed with the body-less 401 (UseStatusCodePages turns it into the http.401 problem) rather than act as nobody, every
    // SuperAdmin rule included. On success, answer with the route's status; otherwise the error's problem (403
    // auth.cannot_manage_super_admin, 404 auth.user_not_found, ...).
    private static async Task<IResult> SendAsync(ICurrentUser currentUser, Func<Guid, Task<Result>> send, IResult success)
    {
        if (currentUser.UserId is not { } actorId)
        {
            return TypedResults.Unauthorized();
        }

        var result = await send(actorId);

        return result.IsSuccess ? success : result.ToProblem();
    }
}
