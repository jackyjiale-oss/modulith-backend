using Microsoft.AspNetCore.Builder;

namespace TemplateName.Infrastructure.Common.Idempotency;

public static class IdempotencyEndpointExtensions
{
    /// <summary>
    /// Opts the endpoint in to <c>Idempotency-Key</c> handling: a retried request with the same key and body gets the first response
    /// again instead of running twice. Requests without the header run as usual.
    /// </summary>
    public static TBuilder WithIdempotency<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.WithMetadata(new IdempotentEndpointMetadata());

    /// <summary>
    /// Adds the <c>Idempotency-Key</c> middleware. Place it after <c>UseRouting</c> (it reads the endpoint's metadata), after
    /// authentication (keys are scoped per user) and before the endpoints. Requires <c>AddInfrastructureCommon</c> and ProblemDetails.
    /// </summary>
    public static IApplicationBuilder UseIdempotency(this IApplicationBuilder app)
        => app.UseMiddleware<IdempotencyMiddleware>();
}
