using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TemplateName.Modules.Auth.Infrastructure.Tokens;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>Maps <c>/.well-known/jwks.json</c> at the root, outside the <c>/api/v1</c> group: the public keys that verify access tokens.</summary>
internal static class JwksEndpoints
{
    internal const string Route = "/.well-known/jwks.json";

    /// <summary>Verifiers may cache the key set for five minutes; a rotation keeps the old key published for longer than that (ADR 0015).</summary>
    internal const string CacheControl = "public, max-age=300";

    internal static IEndpointRouteBuilder MapJwksEndpoints(this IEndpointRouteBuilder app)
    {
        // A standard discovery document, not part of the versioned API, so it stays out of the OpenAPI description like the health checks.
        app.MapGet(Route, GetKeySet)
            .WithName("GetJsonWebKeySet")
            .AllowAnonymous()
            .ExcludeFromDescription();

        return app;
    }

    private static IResult GetKeySet(ISigningKeyProvider keys, HttpResponse response)
    {
        response.Headers.CacheControl = CacheControl;
        return TypedResults.Json(JsonWebKeySetResponse.From(keys.PublicKeySet), contentType: "application/json");
    }
}
