using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Authentication;
using TemplateName.Modules.Auth.Application.ExternalLogins;
using TemplateName.Modules.Auth.Application.ExternalLogins.Link;
using TemplateName.Modules.Auth.Application.ExternalLogins.List;
using TemplateName.Modules.Auth.Application.ExternalLogins.Login;
using TemplateName.Modules.Auth.Application.ExternalLogins.Providers;
using TemplateName.Modules.Auth.Application.ExternalLogins.Unlink;
using TemplateName.Modules.Auth.Domain.Users;
using TemplateName.SharedKernel;
using TemplateName.Web.Common.Results;
using TemplateName.Web.Common.Security;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>Maps the social login routes <c>auth/external/...</c> on the self-service <c>auth</c> group (tag <c>Auth</c>).</summary>
internal static class ExternalLoginEndpoints
{
    internal static RouteGroupBuilder MapExternalLoginEndpoints(this RouteGroupBuilder auth)
    {
        var group = auth.MapGroup("external");

        // What a sign-in screen offers: anonymous, said explicitly (Ruling R13); configuration only, so the global limiter is enough.
        group.MapGet("providers", ListProvidersAsync)
            .WithName("ListExternalProviders")
            .AllowAnonymous()
            .Produces<IReadOnlyList<ExternalProviderResponse>>();

        // A credential route like login: anonymous, limited per client address (auth-strict), and the answer carries tokens, so it is
        // never stored. The provider is matched without regard to case; an unknown and a disabled one are the same 404.
        group.MapPost("{provider}/login", LoginAsync)
            .WithName("ExternalLogin")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.AuthStrict)
            .WithNoStore()
            .Produces<LoginResult>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // Authenticated, said explicitly (Ruling R13). Needs a fresh step-up (403), which is checked before the provider is asked, so only
        // a caller that has proved themselves recently reaches a provider; the global limiter is enough. The provider is matched without
        // regard to case; an unknown and a disabled one are the same 404.
        group.MapPost("{provider}/link", LinkAsync)
            .WithName("LinkExternalLogin")
            .RequireAuthorization()
            .Produces<ExternalLoginResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        // The caller's logins and whether they have a password: at most one login per provider, so not paged.
        group.MapGet("logins", ListLoginsAsync)
            .WithName("ListExternalLogins")
            .RequireAuthorization()
            .Produces<ExternalLoginsResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        // Authenticated, said explicitly (Ruling R13). Needs a fresh step-up (403). The answer has no body.
        group.MapDelete("logins/{id:guid}", UnlinkAsync)
            .WithName("UnlinkExternalLogin")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return auth;
    }

    private static async Task<IResult> ListProvidersAsync(
        IQueryHandler<ListExternalProvidersQuery, IReadOnlyList<ExternalProviderResponse>> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListExternalProvidersQuery(), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    private static async Task<IResult> LoginAsync(
        string provider,
        ExternalLoginRequest request,
        ICommandHandler<ExternalLoginCommand, LoginResult> handler,
        CancellationToken cancellationToken)
    {
        var command = new ExternalLoginCommand(
            provider,
            request.IdToken,
            request.AccessToken,
            request.Nonce,
            request.DisplayName,
            request.Locale,
            request.DeviceName);
        var result = await handler.HandleAsync(command, cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    // 200 with the login (new, or the same identity linked again); 400 validation.failed; 403 auth.step_up_required; 404
    // auth.external_provider_not_found; 409 auth.external_identity_taken or auth.external_provider_already_linked; 401
    // auth.invalid_external_token; 503 auth.external_provider_unavailable. A token whose user is gone or suspended, or whose session is not
    // the caller's active session, gets the body-less 401 of a request without a valid token, as the MFA routes do.
    private static async Task<IResult> LinkAsync(
        string provider,
        LinkExternalLoginRequest request,
        ICommandHandler<LinkExternalLoginCommand, ExternalLoginResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new LinkExternalLoginCommand(provider, request.IdToken, request.AccessToken, request.Nonce), cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        var tokenInvalid = result.Error.Type == ErrorType.NotFound && result.Error.Code != ExternalLoginErrors.ProviderNotFound.Code;
        return tokenInvalid ? TypedResults.Unauthorized() : result.ToProblem();
    }

    // 200 with hasPassword and the logins; a token whose user is gone, soft-deleted or suspended gets the body-less 401.
    private static async Task<IResult> ListLoginsAsync(
        IQueryHandler<ListExternalLoginsQuery, ExternalLoginsResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListExternalLoginsQuery(), cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        return result.Error.Type == ErrorType.NotFound ? TypedResults.Unauthorized() : result.ToProblem();
    }

    // 204; 403 auth.step_up_required; 404 auth.external_login_not_found (an unknown id and another user's id alike); 409
    // auth.last_login_method, or concurrency.conflict when a simultaneous change of the user saved first. The 401 is the same as for link.
    private static async Task<IResult> UnlinkAsync(
        Guid id,
        ICommandHandler<UnlinkExternalLoginCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new UnlinkExternalLoginCommand(id), cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.NoContent();
        }

        var tokenInvalid = result.Error.Type == ErrorType.NotFound && result.Error.Code != ExternalLoginErrors.LoginNotFound.Code;
        return tokenInvalid ? TypedResults.Unauthorized() : result.ToProblem();
    }
}
