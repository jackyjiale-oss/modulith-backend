using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.Mfa.ConfirmTotp;
using TemplateName.Modules.Auth.Application.Mfa.GetMethods;
using TemplateName.Modules.Auth.Application.Mfa.SetUpTotp;
using TemplateName.SharedKernel;
using TemplateName.Web.Common.Results;
using TemplateName.Web.Common.Security;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>Maps the two-step verification routes <c>auth/mfa/...</c> on the self-service <c>auth</c> group (tag <c>Auth</c>).</summary>
internal static class MfaEndpoints
{
    internal static RouteGroupBuilder MapMfaEndpoints(this RouteGroupBuilder auth)
    {
        var group = auth.MapGroup("mfa");

        // Authenticated, said explicitly (Ruling R13). Setup and confirm need a fresh step-up (403) and answer with a secret or recovery
        // codes shown once, so they are never stored.
        group.MapPost("totp/setup", SetUpTotpAsync)
            .WithName("SetUpTotp")
            .RequireAuthorization()
            .WithNoStore()
            .Produces<TotpSetupResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapPost("totp/confirm", ConfirmTotpAsync)
            .WithName("ConfirmTotp")
            .RequireAuthorization()
            .WithNoStore()
            .Produces<RecoveryCodesResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("methods", GetMethodsAsync)
            .WithName("GetMfaMethods")
            .RequireAuthorization()
            .Produces<MfaMethodsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return auth;
    }

    // 200 with the secret (base32 and the otpauth URI), shown once; 400 validation.failed (blank or over-long label); 403
    // auth.step_up_required; 409 auth.mfa_already_enabled, or concurrency.conflict when a simultaneous
    // confirm or setup of the method saved first. A token whose user is gone or suspended, or whose session is not the caller's
    // active session, gets the body-less 401 of a request without a valid token, as PUT me does. The body is optional.
    private static async Task<IResult> SetUpTotpAsync(
        SetUpTotpRequest? request,
        ICommandHandler<SetUpTotpCommand, TotpSetupResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new SetUpTotpCommand(request?.Label), cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        return result.Error.Type == ErrorType.NotFound ? TypedResults.Unauthorized() : result.ToProblem();
    }

    // 200 with the ten recovery codes, shown once; 400 validation.failed (not 6 digits) or auth.invalid_mfa_code (not counted toward
    // the lockout); 403 auth.step_up_required; 409 auth.mfa_setup_not_pending, or concurrency.conflict when a simultaneous confirm or
    // setup restart saved first. The 401 is the same as for setup.
    private static async Task<IResult> ConfirmTotpAsync(
        ConfirmTotpRequest request,
        ICommandHandler<ConfirmTotpCommand, RecoveryCodesResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ConfirmTotpCommand(request.Code), cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        return result.Error.Type == ErrorType.NotFound ? TypedResults.Unauthorized() : result.ToProblem();
    }

    // The caller's methods (at most one, so not paged), whether two-step verification is on and the unused recovery codes left.
    private static async Task<IResult> GetMethodsAsync(
        IQueryHandler<GetMfaMethodsQuery, MfaMethodsResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetMfaMethodsQuery(), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }
}
