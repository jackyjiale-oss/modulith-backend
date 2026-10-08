using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Identity;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Adds <c>GET /test/protected</c> to every host the factory builds, in tests only. The endpoint has no authorization metadata of its
/// own, so only the fallback policy protects it. It is selected ahead of the whole pipeline (the host's <c>UseRouting</c> keeps an
/// endpoint that is already set), so the request still runs through authentication, localization, authorization and the rest in the
/// production order. It answers 200 with what <see cref="ICurrentUser"/> and the request culture say about the caller.
/// </summary>
internal sealed class ProtectedTestEndpointStartupFilter : IStartupFilter
{
    internal const string Route = "/test/protected";

    private static readonly Endpoint ProtectedEndpoint = new(
        WriteCallerAsync,
        new EndpointMetadataCollection(new HttpMethodMetadata([HttpMethods.Get])),
        "Test protected endpoint");

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Equals(Route, StringComparison.OrdinalIgnoreCase))
            {
                context.SetEndpoint(ProtectedEndpoint);
            }

            return nextMiddleware(context);
        });
        next(app);
    };

    private static Task WriteCallerAsync(HttpContext context)
    {
        var currentUser = context.RequestServices.GetRequiredService<ICurrentUser>();
        return context.Response.WriteAsJsonAsync(new CallerResponse(
            currentUser.IsAuthenticated,
            currentUser.UserId,
            currentUser.SessionId,
            CultureInfo.CurrentUICulture.Name,
            CultureInfo.CurrentCulture.Name));
    }

    /// <summary>The body of <c>GET /test/protected</c>.</summary>
    internal sealed record CallerResponse(bool IsAuthenticated, Guid? UserId, Guid? SessionId, string UICulture, string Culture);
}
