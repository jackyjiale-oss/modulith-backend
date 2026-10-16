using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Notifications.Application.Inbox.List;
using TemplateName.Modules.Notifications.Application.Inbox.MarkAllRead;
using TemplateName.Modules.Notifications.Application.Inbox.MarkRead;
using TemplateName.Modules.Notifications.Application.Inbox.UnreadCount;
using TemplateName.Web.Common.Results;

namespace TemplateName.Modules.Notifications.Endpoints;

/// <summary>
/// Maps the signed-in user's routes <c>notifications/...</c> under the host's <c>/api/v1</c> group. Every route is the caller's own: the
/// user is always the token's (<see cref="ICurrentUser"/>), never an id taken from the request.
/// </summary>
internal static class NotificationEndpoints
{
    internal static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("notifications").WithTags("Notifications");

        // A token that carries no user id gets the body-less 401 of a request without a valid token (UseStatusCodePages turns it into
        // the http.401 problem), as Auth's routes do. The check runs after authentication, so it is a safety net, not the gate.
        group.AddEndpointFilter(RequireUserAsync);

        // Authenticated: said explicitly (not only through the fallback policy) so the OpenAPI document shows the bearer requirement.
        group.MapGet("inbox", ListInboxAsync)
            .WithName("ListInbox")
            .RequireAuthorization()
            .Produces<CursorPage<InAppNotificationResponse>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("inbox/unread-count", GetUnreadCountAsync)
            .WithName("GetInboxUnreadCount")
            .RequireAuthorization()
            .Produces<UnreadCountResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("inbox/read-all", MarkAllReadAsync)
            .WithName("MarkAllInboxRead")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("inbox/{id:guid}/read", MarkReadAsync)
            .WithName("MarkInboxNotificationRead")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static ValueTask<object?> RequireUserAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        => context.HttpContext.RequestServices.GetRequiredService<ICurrentUser>().UserId is null
            ? ValueTask.FromResult<object?>(TypedResults.Unauthorized())
            : next(context);

    // Newest first (sort createdAt, ascending or with a leading -); unreadOnly keeps the unread ones. A cursor from one unreadOnly value
    // does not work with the other (400 pagination.cursor_mismatch).
    private static async Task<IResult> ListInboxAsync(
        [AsParameters] ListInboxRequest request,
        IQueryHandler<ListInboxQuery, CursorPage<InAppNotificationResponse>> handler,
        CancellationToken cancellationToken)
    {
        var page = new CursorPageRequest(
            request.PageSize ?? CursorPageRequest.DefaultPageSize,
            request.Cursor,
            request.Sort,
            request.IncludeTotalCount ?? false);
        var result = await handler.HandleAsync(new ListInboxQuery(page, request.UnreadOnly ?? false), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    // 200 { count, isCapped }: count stops at 1000 and isCapped is then true (also for exactly 1000).
    private static async Task<IResult> GetUnreadCountAsync(
        IQueryHandler<GetUnreadCountQuery, UnreadCountResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetUnreadCountQuery(), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    // 204 also when nothing was unread: the notifications that exist now are marked, the ones that arrive later are not.
    private static async Task<IResult> MarkAllReadAsync(ICommandHandler<MarkAllReadCommand> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new MarkAllReadCommand(), cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
    }

    // 204, also when it was already read (the first read time is kept); 404 notifications.notification_not_found for an unknown id and
    // for another user's notification alike (never 403), so ids cannot be probed.
    private static async Task<IResult> MarkReadAsync(Guid id, ICommandHandler<MarkReadCommand> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new MarkReadCommand(id), cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
    }
}
