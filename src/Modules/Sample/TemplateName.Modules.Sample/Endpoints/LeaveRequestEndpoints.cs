using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.Infrastructure.Common.Idempotency;
using TemplateName.Modules.Sample.Application.LeaveRequests.Approve;
using TemplateName.Modules.Sample.Application.LeaveRequests.GetById;
using TemplateName.Modules.Sample.Application.LeaveRequests.List;
using TemplateName.Modules.Sample.Application.LeaveRequests.Submit;
using TemplateName.Web.Common.Results;

namespace TemplateName.Modules.Sample.Endpoints;

/// <summary>Maps <c>sample/leave-requests</c> under the host's <c>/api/v1</c> group.</summary>
internal static class LeaveRequestEndpoints
{
    private const string GetByIdRouteName = "GetLeaveRequestById";

    internal static IEndpointRouteBuilder MapLeaveRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("sample/leave-requests").WithTags("Sample");

        group.MapPost("/", SubmitAsync)
            .WithName("SubmitLeaveRequest")
            .WithIdempotency()
            .Produces<SubmitLeaveRequestResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

        group.MapGet("/", ListAsync)
            .WithName("ListLeaveRequests")
            .Produces<CursorPage<LeaveRequestListItemResponse>>()
            .ProducesValidationProblem();

        group.MapGet("/{id:guid}", GetByIdAsync)
            .WithName(GetByIdRouteName)
            .Produces<LeaveRequestResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapPost("/{id:guid}/approve", ApproveAsync)
            .WithName("ApproveLeaveRequest")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
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
        ApproveLeaveRequestRequest request,
        ICommandHandler<ApproveLeaveRequestCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ApproveLeaveRequestCommand(id, request.ApproverId), cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
    }
}
