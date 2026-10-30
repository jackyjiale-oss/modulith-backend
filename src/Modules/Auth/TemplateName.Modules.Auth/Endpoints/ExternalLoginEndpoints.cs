using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Authentication;
using TemplateName.Modules.Auth.Application.ExternalLogins.Login;
using TemplateName.Modules.Auth.Application.ExternalLogins.Providers;
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
}
