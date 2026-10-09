using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TemplateName.Application.Common.Messaging;
using TemplateName.Application.Common.Pagination;
using TemplateName.Modules.Auth.Application.Authentication.Login;
using TemplateName.Modules.Auth.Application.Authentication.Logout;
using TemplateName.Modules.Auth.Application.Authentication.LogoutAll;
using TemplateName.Modules.Auth.Application.Authentication.Refresh;
using TemplateName.Modules.Auth.Application.Me.GetMe;
using TemplateName.Modules.Auth.Application.Me.Update;
using TemplateName.Modules.Auth.Application.Passwords.Change;
using TemplateName.Modules.Auth.Application.Passwords.Forgot;
using TemplateName.Modules.Auth.Application.Passwords.Reset;
using TemplateName.Modules.Auth.Application.Registration.ConfirmEmail;
using TemplateName.Modules.Auth.Application.Registration.Register;
using TemplateName.Modules.Auth.Application.Registration.ResendConfirmation;
using TemplateName.Modules.Auth.Application.Sessions.List;
using TemplateName.Modules.Auth.Application.Sessions.Revoke;
using TemplateName.SharedKernel;
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
            .WithNoStore()
            .Produces<LoginResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("token/refresh", RefreshTokenAsync)
            .WithName("RefreshToken")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.AuthStrict)
            .WithNoStore()
            .Produces<LoginResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("password/forgot", ForgotPasswordAsync)
            .WithName("ForgotPassword")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.AuthStrict)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        group.MapPost("password/reset", ResetPasswordAsync)
            .WithName("ResetPassword")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.AuthStrict)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        // Authenticated: said explicitly (not only through the fallback policy) so the OpenAPI document shows the bearer requirement.
        group.MapGet("me", GetMeAsync)
            .WithName("GetCurrentUser")
            .RequireAuthorization()
            .WithNoStore()
            .Produces<MeResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapPut("me", UpdateProfileAsync)
            .WithName("UpdateProfile")
            .RequireAuthorization()
            .Produces<MeResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapGet("sessions", ListSessionsAsync)
            .WithName("ListSessions")
            .RequireAuthorization()
            .Produces<CursorPage<SessionResponse>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        group.MapDelete("sessions/{id:guid}", RevokeSessionAsync)
            .WithName("RevokeSession")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

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

        // Authenticated, but it checks a password, so it is also limited per client address like the anonymous credential routes: a
        // stolen access token cannot guess the current password at the global per-user rate.
        group.MapPost("password/change", ChangePasswordAsync)
            .WithName("ChangePassword")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.AuthStrict)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return app;
    }

    // 202 with no body whether a link was sent or not (unknown or suspended address, cooldown).
    private static async Task<IResult> ForgotPasswordAsync(
        ForgotPasswordRequest request,
        ICommandHandler<ForgotPasswordCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ForgotPasswordCommand(request.Email), cancellationToken);

        return result.IsSuccess ? TypedResults.Accepted((string?)null) : result.ToProblem();
    }

    // 204, or 400 auth.invalid_token (unknown, used, replaced, expired, another purpose's token, or an account that is gone or
    // suspended, all alike), auth.password_breached or auth.password_reused.
    private static async Task<IResult> ResetPasswordAsync(
        ResetPasswordRequest request,
        ICommandHandler<ResetPasswordCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ResetPasswordCommand(request.Token, request.NewPassword), cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
    }

    // 204, or 400 auth.current_password_incorrect, auth.password_breached or auth.password_reused. A token whose user is gone or
    // suspended gets the body-less 401 of a request without a valid token, as GET me does.
    private static async Task<IResult> ChangePasswordAsync(
        ChangePasswordRequest request,
        ICommandHandler<ChangePasswordCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ChangePasswordCommand(request.CurrentPassword, request.NewPassword), cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.NoContent();
        }

        return result.Error.Type == ErrorType.NotFound ? TypedResults.Unauthorized() : result.ToProblem();
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

    // 200 with the updated profile as GET me returns it; a token whose user is gone or suspended gets the body-less 401 of a request
    // without a valid token, as GET me does. The new language reaches the access token with the next refresh.
    private static async Task<IResult> UpdateProfileAsync(
        UpdateProfileRequest request,
        ICommandHandler<UpdateProfileCommand, MeResponse> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new UpdateProfileCommand(request.DisplayName, request.Locale, request.TimeZone), cancellationToken);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        return result.Error.Type == ErrorType.NotFound ? TypedResults.Unauthorized() : result.ToProblem();
    }

    // The caller's own active sessions, newest activity first by default (sort lastSeenAt or createdAt, ascending or with a leading -).
    private static async Task<IResult> ListSessionsAsync(
        [AsParameters] ListSessionsRequest request,
        IQueryHandler<ListSessionsQuery, CursorPage<SessionResponse>> handler,
        CancellationToken cancellationToken)
    {
        var page = new CursorPageRequest(
            request.PageSize ?? CursorPageRequest.DefaultPageSize,
            request.Cursor,
            request.Sort,
            request.IncludeTotalCount ?? false);
        var result = await handler.HandleAsync(new ListSessionsQuery(page), cancellationToken);

        return result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();
    }

    // 204, also when the caller's session had already ended; 404 auth.session_not_found for an unknown id and for another user's
    // session alike (never 403), so ids cannot be probed.
    private static async Task<IResult> RevokeSessionAsync(
        Guid id,
        ICommandHandler<RevokeSessionCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RevokeSessionCommand(id), cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : result.ToProblem();
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
