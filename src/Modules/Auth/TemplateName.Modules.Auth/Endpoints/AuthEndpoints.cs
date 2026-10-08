using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TemplateName.Application.Common.Messaging;
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

        return app;
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
