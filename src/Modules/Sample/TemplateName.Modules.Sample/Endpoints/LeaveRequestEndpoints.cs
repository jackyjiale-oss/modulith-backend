using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TemplateName.Application.Common.Identity;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.Infrastructure.Common.Idempotency;
using TemplateName.Modules.Sample.Application;
using TemplateName.Modules.Sample.Application.LeaveRequests.Approve;
using TemplateName.Modules.Sample.Application.LeaveRequests.GetById;
using TemplateName.Modules.Sample.Application.LeaveRequests.List;
using TemplateName.Modules.Sample.Application.LeaveRequests.Submit;
using TemplateName.Web.Common.Results;
using TemplateName.Web.Common.Security;

namespace TemplateName.Modules.Sample.Endpoints;

/// <summary>Maps <c>sample/leave-requests</c> under the host's <c>/api/v1</c> group.</summary>
internal static class LeaveRequestEndpoints
{
    private const string GetByIdRouteName = "GetLeaveRequestById";

    internal static IEndpointRouteBuilder MapLeaveRequestEndpoints(this IEndpointRouteBuilder app)
    {
        // Every route needs a signed-in caller with its permission: 401 without a valid token, 403 without the permission.
        var group = app.MapGroup("sample/leave-requests")
            .WithTags("Sample")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/", SubmitAsync)
            .WithName("SubmitLeaveRequest")
            .RequirePermission(SamplePermissions.LeaveRequestCreate)
            .WithIdempotency()
            .Produces<SubmitLeaveRequestResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        group.MapGet("/", ListAsync)
            .WithName("ListLeaveRequests")
            .RequirePermission(SamplePermissions.LeaveRequestView)
            .Produces<CursorPage<LeaveRequestListItemResponse>>()
            .ProducesValidationProblem();

        group.MapGet("/{id:guid}", GetByIdAsync)
            .WithName(GetByIdRouteName)
            .RequirePermission(SamplePermissions.LeaveRequestView)
            .Produces<LeaveRequestResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        // No body: the approver is the caller, never a value the client sends.
        group.MapPost("/{id:guid}/approve", ApproveAsync)
            .WithName("ApproveLeaveRequest")
            .RequirePermission(SamplePermissions.LeaveRequestApprove)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> SubmitAsync(
        SubmitLeaveRequestRequest request,
        ICommandHandler<SubmitLeaveRequestCommand, Guid> handler,
        LinkGenerator links,
        CancellationToken cancellationToken)
    {
        var command = new SubmitLeaveRequestCommand(request.EmployeeId, request.StartDate, request.EndDate, request.Reason);
        var result = await handler.HandleAsync(command, cancellationToken);
        if (result.IsFailure)
        {
            return result.ToProblem();
        }

        // A path (/api/v1/sample/leave-requests/{id}), not an absolute URI, so it holds behind proxies.
        var location = links.GetPathByName(GetByIdRouteName, new { id = result.Value });
        return TypedResults.Created(location, new SubmitLeaveRequestResponse(result.Value));
    }

    private static async Task<IResult> ListAsync(
        [AsParameters] ListLeaveRequestsRequest request,
        IQueryHandler<ListLeaveRequestsQuery, CursorPage<LeaveRequestListItemResponse>> handler,
        CancellationToken cancellationToken)
    {
        var page = new CursorPageRequest(
            request.PageSize ?? CursorPageRequest.DefaultPageSize,
            request.Cursor,
            request.Sort,
            request.IncludeTotalCount ?? false);
        var result = await handler.HandleAsync(new ListLeaveRequestsQuery(request.EmployeeId, request.Status, page), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        IQueryHandler<GetLeaveRequestByIdQuery, LeaveRequestResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetLeaveRequestByIdQuery(id), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> ApproveAsync(
        Guid id,
        ICurrentUser currentUser,
        ICommandHandler<ApproveLeaveRequestCommand> handler,
        CancellationToken cancellationToken)
    {
        // The permission policy only lets a signed-in user with an id through; without one, fail closed rather than approve as nobody.
        // The body-less 401 becomes the localized http.401 problem in UseStatusCodePages.
        if (currentUser.UserId is not { } approverId)
        {
            return TypedResults.Unauthorized();
        }

        var result = await handler.HandleAsync(new ApproveLeaveRequestCommand(id, approverId), cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
    }
}
