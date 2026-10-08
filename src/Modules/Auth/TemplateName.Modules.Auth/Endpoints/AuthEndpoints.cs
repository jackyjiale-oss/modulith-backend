using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Authentication.Login;
using TemplateName.Modules.Auth.Application.Authentication.Logout;
using TemplateName.Modules.Auth.Application.Authentication.LogoutAll;
using TemplateName.Modules.Auth.Application.Authentication.Refresh;
using TemplateName.Modules.Auth.Application.Me.GetMe;
using TemplateName.Modules.Auth.Application.Registration.ConfirmEmail;
using TemplateName.Modules.Auth.Application.Registration.Register;
using TemplateName.Modules.Auth.Application.Registration.ResendConfirmation;
using TemplateName.Web.Common.Results;
using TemplateName.Web.Common.Security;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>Maps the self-service routes <c>auth/...</c> under the host's <c>/api/v1</c> group.</summary>
internal static class AuthEndpoints
{
    internal static IEndpointRouteBuilder MapSelfServiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("auth").WithTags("Auth");

        // Anonymous credential endpoints: each says AllowAnonymous explicitly and is limited per client address (auth-strict).
        group.MapPost("register", RegisterAsync)
            .WithName("Register")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.AuthStrict)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("email/confirm", ConfirmEmailAsync)
            .WithName("ConfirmEmail")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.AuthStrict)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("email/resend-confirmation", ResendConfirmationAsync)
            .WithName("ResendConfirmation")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.AuthStrict)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("login", LoginAsync)
            .WithName("Login")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.AuthStrict)
            .Produces<LoginResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("token/refresh", RefreshTokenAsync)
            .WithName("RefreshToken")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.AuthStrict)
            .Produces<LoginResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // Authenticated: said explicitly (not only through the fallback policy) so the OpenAPI document shows the bearer requirement.
        group.MapGet("me", GetMeAsync)
            .WithName("GetCurrentUser")
            .RequireAuthorization()
            .Produces<MeResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("logout", LogoutAsync)
            .WithName("Logout")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPost("logout-all", LogoutAllAsync)
            .WithName("LogoutAll")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return app;
    }

    // 200 with the next token pair of the same session; 401 auth.invalid_refresh_token (unknown token or ended session, alike),
    // auth.refresh_token_expired or auth.refresh_token_reused (the session is revoked).
    private static async Task<IResult> RefreshTokenAsync(
        RefreshTokenRequest request,
        ICommandHandler<RefreshTokenCommand, LoginResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RefreshTokenCommand(request.RefreshToken), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    // 204 also when the session was already ended: logging out twice is not an error.
    private static async Task<IResult> LogoutAsync(ICommandHandler<LogoutCommand> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new LogoutCommand(), cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
    }

    private static async Task<IResult> LogoutAllAsync(ICommandHandler<LogoutAllCommand> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new LogoutAllCommand(), cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
    }

    // 401 auth.invalid_credentials for every wrong combination (unknown email, wrong password, no password set, locked account);
    // 403 only after a correct password.
    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        ICommandHandler<LoginCommand, LoginResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new LoginCommand(request.Email, request.Password, request.DeviceName), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    // The handler fails only when the token's user can no longer sign in (unknown, soft-deleted or suspended). Fail closed with the same
    // body-less 401 a request without a valid token gets; UseStatusCodePages turns it into the http.401 problem.
    private static async Task<IResult> GetMeAsync(IQueryHandler<GetMeQuery, MeResponse> handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetMeQuery(), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : TypedResults.Unauthorized();
    }

    // 202 with no body for a new and for a known address alike: the answer must not tell them apart.
    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        ICommandHandler<RegisterCommand> handler,
        CancellationToken cancellationToken)
    {
        var command = new RegisterCommand(
            request.Email,
            request.Password,
            request.DisplayName,
            request.Locale ?? CultureInfo.CurrentUICulture.Name);
        var result = await handler.HandleAsync(command, cancellationToken);

        return result.IsSuccess ? TypedResults.Accepted((string?)null) : result.ToProblem();
    }

    private static async Task<IResult> ConfirmEmailAsync(
        ConfirmEmailRequest request,
        ICommandHandler<ConfirmEmailCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ConfirmEmailCommand(request.Token), cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
    }

    // 202 with no body whether a code was sent or not (unknown or confirmed address, cooldown).
    private static async Task<IResult> ResendConfirmationAsync(
        ResendConfirmationRequest request,
        ICommandHandler<ResendConfirmationCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ResendConfirmationCommand(request.Email), cancellationToken);

        return result.IsSuccess ? TypedResults.Accepted((string?)null) : result.ToProblem();
    }
}
