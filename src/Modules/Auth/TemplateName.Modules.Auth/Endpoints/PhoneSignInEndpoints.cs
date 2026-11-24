using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Application.Common.Messaging;
using TemplateName.Modules.Auth.Application.Phone;
using TemplateName.Modules.Auth.Application.PhoneSignIn.Send;
using TemplateName.Web.Common.Results;
using TemplateName.Web.Common.Security;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>
/// Signing in with a code texted to the account's verified number (spec: phone-number sign-in). Both routes are anonymous and limited
/// per client address like the credential routes; while <c>Auth:Phone:SignInEnabled</c> is off each answers 404 <c>http.404</c>, as an
/// unknown route does.
/// </summary>
internal static class PhoneSignInEndpoints
{
    internal static RouteGroupBuilder MapPhoneSignInEndpoints(this RouteGroupBuilder auth)
    {
        // Answers 202 for every well-formed number, known or not, so the route never says which numbers have accounts.
        auth.MapPost("phone/sign-in/send", SendAsync)
            .WithName("SendPhoneSignInCode")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.AuthStrict)
            .AddEndpointFilter(NotFoundWhileDisabledAsync)
            .Produces(StatusCodes.Status202Accepted)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return auth;
    }

    private static async ValueTask<object?> NotFoundWhileDisabledAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        => context.HttpContext.RequestServices.GetRequiredService<IOptions<PhoneOptions>>().Value.SignInEnabled
            ? await next(context)
            : TypedResults.Problem(statusCode: StatusCodes.Status404NotFound);

    private static async Task<IResult> SendAsync(
        SendPhoneSignInCodeRequest request,
        ICommandHandler<SendPhoneSignInCodeCommand> handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new SendPhoneSignInCodeCommand(request.PhoneNumber), cancellationToken);
        return result.IsSuccess ? TypedResults.Accepted((string?)null) : result.ToProblem();
    }
}
