using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace TemplateName.Modules.Auth.Endpoints;

/// <summary>
/// Marks the responses of an endpoint as never to be stored: <c>Cache-Control: no-store</c> (RFC 9111) and, for HTTP/1.0 caches,
/// <c>Pragma: no-cache</c>. For the routes whose answers carry tokens (login, refresh) or the signed-in user's data (<c>GET me</c>), so
/// neither a browser nor an intermediary keeps them. Set on every answer of the endpoint, refusals included, so the header tells nothing
/// about the outcome. Not applied globally: most answers carry nothing secret and may be cached by their own rules.
/// </summary>
internal static class NoStoreEndpointExtensions
{
    internal const string CacheControl = "no-store";
    internal const string Pragma = "no-cache";

    public static TBuilder WithNoStore<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
        => builder.AddEndpointFilter(static (context, next) =>
        {
            var headers = context.HttpContext.Response.Headers;
            headers.CacheControl = CacheControl;
            headers.Pragma = Pragma;
            return next(context);
        });
}
