using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Audit.Application;
using TemplateName.Modules.Audit.Application.Trails.List;
using TemplateName.Web.Common.Results;
using TemplateName.Web.Common.Security;

namespace TemplateName.Modules.Audit.Endpoints;

/// <summary>Maps the audit trail route <c>admin/audit/trails</c> under the host's <c>/api/v1</c> group.</summary>
internal static class AdminAuditTrailEndpoints
{
    internal static IEndpointRouteBuilder MapAdminAuditTrailEndpoints(this IEndpointRouteBuilder app)
    {
        // The route needs a signed-in caller with audit.trails.view (R13): 401 without a valid token, 403 without the permission.
        var group = app.MapGroup("admin/audit/trails")
            .WithTags("Audit administration")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/", ListAsync)
            .WithName("ListAuditTrails")
            .RequirePermission(AuditPermissions.TrailsView)
            .Produces<CursorPage<AuditTrailResponse>>()
            .ProducesValidationProblem();

        return app;
    }

    // Newest first by default (sort occurredAt, ascending or with a leading -); the filters are entityType, entityId, userId,
    // changeSetId, module, action and an inclusive from and to (ISO 8601; without an offset UTC).
    private static async Task<IResult> ListAsync(
        [AsParameters] ListAuditTrailsRequest request,
        IQueryHandler<ListAuditTrailsQuery, CursorPage<AuditTrailResponse>> handler,
        CancellationToken cancellationToken)
    {
        var page = new CursorPageRequest(
            request.PageSize ?? CursorPageRequest.DefaultPageSize,
            request.Cursor,
            request.Sort,
            request.IncludeTotalCount ?? false);
        var query = new ListAuditTrailsQuery(
            page,
            request.EntityType,
            request.EntityId,
            request.UserId,
            request.ChangeSetId,
            request.Module,
            request.Action,
            request.From,
            request.To);
        var result = await handler.HandleAsync(query, cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }
}
