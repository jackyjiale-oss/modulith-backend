using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Phone;
using TemplateName.Modules.Auth.Application.Phone.Confirm;
using TemplateName.Modules.Auth.Application.Phone.RequestCode;
using TemplateName.Modules.Auth.Application.Phone.Remove;
using TemplateName.Modules.Auth.Domain.Verification;
using TemplateName.SharedKernel;
using TemplateName.Web.Common.Results;
using TemplateName.Web.Common.Security;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>
/// Maps the phone number routes <c>auth/me/phone</c> on the self-service <c>auth</c> group (tag <c>Auth</c>). Signed in, said
/// explicitly; while <c>Auth:Phone:Enabled</c> is off each answers 404 <c>http.404</c>, as an unknown route does.
/// </summary>
internal static class PhoneEndpoints
{
    internal static RouteGroupBuilder MapPhoneEndpoints(this RouteGroupBuilder auth)
    {
        // Each request may send a paid text, so it is also limited per client address like the credential routes; the cooldown and
        // the daily cap per user are the handler's.
        auth.MapPost("me/phone", RequestCodeAsync)
            .WithName("RequestPhoneCode")
            .RequireAuthorization()
            .RequireRateLimiting(RateLimitPolicies.AuthStrict)
            .AddEndpointFilter(NotFoundWhileDisabledAsync)
            .Produces<PhoneCodeResponse>(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("me/phone/confirm", ConfirmAsync)
            .WithName("ConfirmPhone")
            .RequireAuthorization()
            .AddEndpointFilter(NotFoundWhileDisabledAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapDelete("me/phone", RemoveAsync)
            .WithName("RemovePhone")
            .RequireAuthorization()
            .AddEndpointFilter(NotFoundWhileDisabledAsync)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return auth;
    }

    private static async ValueTask<object?> NotFoundWhileDisabledAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        => context.HttpContext.RequestServices.GetRequiredService<IOptions<PhoneOptions>>().Value.Enabled
            ? await next(context)
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);

    private static async Task<IResult> RequestCodeAsync(
        RequestPhoneCodeRequest request,
        ICommandHandler<RequestPhoneCodeCommand, PhoneCodeResponse> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RequestPhoneCodeCommand(request.PhoneNumber), cancellationToken);

        return result.IsSuccess
            ? TypedResults.Accepted((string?)null, result.Value)
            : ToProblem(result, httpContext);
    }

    private static async Task<IResult> ConfirmAsync(
        ConfirmPhoneRequest request,
        ICommandHandler<ConfirmPhoneCommand> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ConfirmPhoneCommand(request.Code), cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : ToProblem(result, httpContext);
    }

    private static async Task<IResult> RemoveAsync(
        ICommandHandler<RemovePhoneCommand> handler,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new RemovePhoneCommand(), cancellationToken);

        return result.IsSuccess ? TypedResults.NoContent() : ToProblem(result, httpContext);
    }

    // A caller whose account or session is gone is answered like a request without a valid token; the cooldown and the daily cap like
    // the host's rate limiter (429 rate_limit.exceeded with Retry-After).
    private static IResult ToProblem(Result result, HttpContext httpContext)
    {
        switch (result.Error)
        {
            case { Type: ErrorType.NotFound }:
                return TypedResults.Unauthorized();

            case TooManyCodesError tooMany:
                var seconds = Math.Max(1, (int)Math.Ceiling(tooMany.RetryAfter.TotalSeconds));
                httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status429TooManyRequests,
                    detail: tooMany.Message,
                    extensions: new Dictionary<string, object?> { ["code"] = tooMany.Code });

            default:
                return result.ToProblem();
        }
    }
}
