using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Auth.Application.TrustedDevices.List;
using TemplateName.Modules.Auth.Application.TrustedDevices.Revoke;
using TemplateName.Web.Common.Results;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>Maps the trusted device routes <c>auth/trusted-devices/...</c> on the self-service <c>auth</c> group (tag <c>Auth</c>).</summary>
internal static class TrustedDeviceEndpoints
{
    internal static RouteGroupBuilder MapTrustedDeviceEndpoints(this RouteGroupBuilder auth)
    {
        // Authenticated, said explicitly (Ruling R13). Neither route needs a step-up: listing shows no secret, and forgetting a device
        // only reduces trust (plan decision D11). Neither answer carries a token, so there is nothing to keep out of a cache.
        auth.MapGet("trusted-devices", ListAsync)
            .WithName("ListTrustedDevices")
            .RequireAuthorization()
            .Produces<CursorPage<TrustedDeviceResponse>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapDelete("trusted-devices/{id:guid}", RevokeAsync)
            .WithName("RevokeTrustedDevice")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return auth;
    }

    // The caller's usable devices (not revoked, not expired, bound to the current security stamp), newest first by default (sort createdAt,
    // ascending or with a leading -).
    private static async Task<IResult> ListAsync(
        [AsParameters] ListTrustedDevicesRequest request,
        IQueryHandler<ListTrustedDevicesQuery, CursorPage<TrustedDeviceResponse>> handler,
        CancellationToken cancellationToken)
    {
        var page = new CursorPageRequest(
            request.PageSize ?? CursorPageRequest.DefaultPageSize,
            request.Cursor,
            request.Sort,
            request.IncludeTotalCount ?? false);
        var result = await handler.HandleAsync(new ListTrustedDevicesQuery(page), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    // 204, also when the caller's device was already revoked; 404 auth.trusted_device_not_found for an unknown id and for another user's
    // device alike (never 403), so ids cannot be probed.
    private static async Task<IResult> RevokeAsync(
        Guid id,
        ICommandHandler<RevokeTrustedDeviceCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RevokeTrustedDeviceCommand(id), cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
    }
}
