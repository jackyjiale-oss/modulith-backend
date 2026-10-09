using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Application.Admin.AuditLogs.List;
using TemplateName.Web.Common.Results;
using TemplateName.Web.Common.Security;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>Maps the audit log route <c>admin/auth/audit-logs</c> under the host's <c>/api/v1</c> group.</summary>
internal static class AdminAuditLogEndpoints
{
    internal static IEndpointRouteBuilder MapAdminAuditLogEndpoints(this IEndpointRouteBuilder app)
    {
        // The route needs a signed-in caller with auth.audit.view (R13): 401 without a valid token, 403 without the permission.
        var group = app.MapGroup("admin/auth/audit-logs")
            .WithTags("Auth administration")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapGet("/", ListAsync)
            .WithName("ListAuditLogs")
            .RequirePermission(AuthPermissions.AuditView)
            .Produces<CursorPage<AuditLogResponse>>()
            .ProducesValidationProblem();

        return app;
    }

    // Newest first by default (sort occurredAt, ascending or with a leading -); the filters are userId, eventType, succeeded and an
    // inclusive from and to (ISO 8601; without an offset UTC).
    private static async Task<IResult> ListAsync(
        [AsParameters] ListAuditLogsRequest request,
        IQueryHandler<ListAuditLogsQuery, CursorPage<AuditLogResponse>> handler,
        CancellationToken cancellationToken)
    {
        var page = new CursorPageRequest(
            request.PageSize ?? CursorPageRequest.DefaultPageSize,
            request.Cursor,
            request.Sort,
            request.IncludeTotalCount ?? false);
        var query = new ListAuditLogsQuery(request.UserId, request.EventType, request.Succeeded, request.From, request.To, page);
        var result = await handler.HandleAsync(query, cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }
}
