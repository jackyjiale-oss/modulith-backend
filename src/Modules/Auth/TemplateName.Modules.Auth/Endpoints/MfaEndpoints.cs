using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Mfa;
using TemplateName.Modules.Auth.Application.Mfa.ConfirmTotp;
using TemplateName.Modules.Auth.Application.Mfa.GetMethods;
using TemplateName.Modules.Auth.Application.Mfa.RegenerateRecoveryCodes;
using TemplateName.Modules.Auth.Application.Mfa.RemoveMethod;
using TemplateName.Modules.Auth.Application.Mfa.SetUpTotp;
using TemplateName.Modules.Auth.Application.Mfa.Verify;
using TemplateName.Modules.Auth.Domain.Mfa;
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

        // The second step of a sign-in: anonymous (the caller has no token yet), said explicitly (Ruling R13), and it checks a code that
        // counts toward the lockout, so it is limited per client address like login; the answer carries tokens, so it is never stored.
        group.MapPost("verify", VerifyAsync)
            .WithName("VerifyMfa")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.AuthStrict)
            .WithNoStore()
            .Produces<MfaVerifyResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

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

        // Authenticated, said explicitly (Ruling R13). Removing a confirmed method turns two-step verification off, so it needs a fresh
        // step-up (403); a pending setup is cancelled without one. The answer has no body, so there is nothing to keep out of a cache.
        group.MapDelete("methods/{id:guid}", RemoveMethodAsync)
            .WithName("RemoveMfaMethod")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        // Authenticated, said explicitly (Ruling R13). Needs a fresh step-up (403) and answers with recovery codes shown once, so it is
        // never stored.
        group.MapPost("recovery-codes/regenerate", RegenerateRecoveryCodesAsync)
            .WithName("RegenerateRecoveryCodes")
            .RequireAuthorization()
            .WithNoStore()
            .Produces<RecoveryCodesResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return auth;
    }

    // 200 with the tokens of a new session (and, with rememberDevice while trusted devices are on, a trusted device token shown once);
    // 400 validation.failed (blank or over-long challenge token, not exactly one of a 6-digit code
    // and a recovery code), auth.invalid_mfa_code (counted on the challenge and toward the lockout) or auth.invalid_mfa_challenge (unknown,
    // expired, used, exhausted, or the account changed, locked or gone: all alike, nothing counted).
    private static async Task<IResult> VerifyAsync(
        VerifyMfaRequest request,
        ICommandHandler<VerifyMfaCommand, MfaVerifyResponse> handler,
        CancellationToken cancellationToken)
    {
        var command = new VerifyMfaCommand(request.ChallengeToken, request.Code, request.RecoveryCode, request.RememberDevice ?? false);
        var result = await handler.HandleAsync(command, cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
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

    // 204; 404 auth.mfa_method_not_found (an unknown id and another user's id alike, whatever the caller has proved); 403
    // auth.step_up_required (a confirmed method only); 409 concurrency.conflict when a simultaneous confirm or removal saved first. A
    // token whose user is gone or suspended, or whose session is not the caller's active session, gets the body-less 401 of a request
    // without a valid token, as setup does.
    private static async Task<IResult> RemoveMethodAsync(
        Guid id,
        ICommandHandler<RemoveMfaMethodCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RemoveMfaMethodCommand(id), cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.NoContent();
        }

        var tokenInvalid = result.Error.Type == ErrorType.NotFound && result.Error.Code != MfaErrors.MethodNotFoundCode;
        return tokenInvalid ? TypedResults.Unauthorized() : result.ToProblem();
    }

    // 200 with the ten new recovery codes, shown once (the old ones stop working); 403 auth.step_up_required; 409
    // auth.mfa_not_enabled (no confirmed method), or concurrency.conflict when a simultaneous change saved first. The 401 is the same as
    // for removal.
    private static async Task<IResult> RegenerateRecoveryCodesAsync(
        ICommandHandler<RegenerateRecoveryCodesCommand, RecoveryCodesResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RegenerateRecoveryCodesCommand(), cancellationToken);

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
