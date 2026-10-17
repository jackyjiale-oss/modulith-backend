using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Notifications.Application;
using TemplateName.Modules.Notifications.Application.Admin.Deliveries.List;
using TemplateName.Modules.Notifications.Application.Admin.Deliveries.Retry;
using TemplateName.Web.Common.Results;
using TemplateName.Web.Common.Security;

namespace TemplateName.Modules.Notifications.Endpoints;

/// <summary>Maps the delivery administration routes <c>admin/notifications/deliveries</c> under the host's <c>/api/v1</c> group.</summary>
internal static class AdminDeliveryEndpoints
{
    internal static IEndpointRouteBuilder MapAdminDeliveryEndpoints(this IEndpointRouteBuilder app)
    {
        // Each route needs a signed-in caller with its own permission (R13): 401 without a valid token, 403 without the permission.
        var group = app.MapGroup("admin/notifications/deliveries")
            .WithTags("Notifications administration")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/", ListAsync)
            .WithName("ListDeliveries")
            .RequirePermission(NotificationsPermissions.DeliveryView)
            .Produces<CursorPage<DeliveryResponse>>()
            .ProducesValidationProblem();

        group.MapPost("/{id:guid}/retry", RetryAsync)
            .WithName("RetryDelivery")
            .RequirePermission(NotificationsPermissions.DeliveryRetry)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    // Newest first by default (sort createdAt, ascending or with a leading -); the filters are status, channel, typeCode and
    // recipientUserId. The destination is masked (a****@example.com); the real address is never returned.
    private static async Task<IResult> ListAsync(
        [AsParameters] ListDeliveriesRequest request,
        IQueryHandler<ListDeliveriesQuery, CursorPage<DeliveryResponse>> handler,
        CancellationToken cancellationToken)
    {
        var page = new CursorPageRequest(
            request.PageSize ?? CursorPageRequest.DefaultPageSize,
            request.Cursor,
            request.Sort,
            request.IncludeTotalCount ?? false);
        var query = new ListDeliveriesQuery(page, request.Status, request.Channel, request.TypeCode, request.RecipientUserId);
        var result = await handler.HandleAsync(query, cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    // 202, no body: the delivery is pending again and the delivery worker sends it on its next poll. 404 notifications.delivery_not_found;
    // 409 notifications.delivery_not_retryable (not dead-lettered, which includes a delivery another request just retried) or
    // notifications.delivery_expired (the link or notice is past its expiry).
    private static async Task<IResult> RetryAsync(Guid id, ICommandHandler<RetryDeliveryCommand> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RetryDeliveryCommand(id), cancellationToken);

        return result.IsSuccess ? TypedResults.Accepted((string?)null) : result.ToProblem();
    }
}
