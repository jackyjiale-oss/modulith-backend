using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Identity;
using TemplateName.Web.Common.Security;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Adds <c>GET /test/protected</c> to every host the factory builds, in tests only. The endpoint has no authorization metadata of its
/// own, so only the fallback policy protects it. It is selected ahead of the whole pipeline (the host's <c>UseRouting</c> keeps an
/// endpoint that is already set), so the request still runs through authentication, localization, authorization and the rest in the
/// production order. It answers 200 with what <see cref="ICurrentUser"/> and the request culture say about the caller.
/// <c>GET /test/permission</c> is selected the same way but carries <c>RequirePermission</c> for a permission nobody holds, so a signed-in
/// caller always gets 403 from it.
/// </summary>
internal sealed class ProtectedTestEndpointStartupFilter : IStartupFilter
{
    internal const string Route = "/test/protected";

    /// <summary>An endpoint that needs <see cref="UngrantedPermission"/>, which no role has been given.</summary>
    internal const string PermissionRoute = "/test/permission";

    internal const string UngrantedPermission = "test.resource.read";

    private static readonly Endpoint ProtectedEndpoint = new(
        WriteCallerAsync,
        new EndpointMetadataCollection(new HttpMethodMetadata([HttpMethods.Get])),
        "Test protected endpoint");

    private static readonly Endpoint PermissionEndpoint = CreatePermissionEndpoint();

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (HttpMethods.IsGet(context.Request.Method))
            {
                if (context.Request.Path.Equals(Route, StringComparison.OrdinalIgnoreCase))
                {
                    context.SetEndpoint(ProtectedEndpoint);
                }
                else if (context.Request.Path.Equals(PermissionRoute, StringComparison.OrdinalIgnoreCase))
                {
                    context.SetEndpoint(PermissionEndpoint);
                }
            }

            return nextMiddleware(context);
        });
        next(app);
    };

    private static RouteEndpoint CreatePermissionEndpoint()
    {
        // Let the production RequirePermission convention write the metadata, so the test covers the real thing.
        var builder = new RouteEndpointBuilder(WriteCallerAsync, RoutePatternFactory.Parse(PermissionRoute), order: 0)
        {
            DisplayName = "Test permission endpoint",
        };
        builder.Metadata.Add(new HttpMethodMetadata([HttpMethods.Get]));
        var conventions = new ConventionCollector();
        conventions.RequirePermission(UngrantedPermission);
        conventions.ApplyTo(builder);
        return (RouteEndpoint)builder.Build();
    }

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

    private sealed class ConventionCollector : IEndpointConventionBuilder
    {
        private readonly List<Action<EndpointBuilder>> _conventions = [];

        public void Add(Action<EndpointBuilder> convention) => _conventions.Add(convention);

        public void Finally(Action<EndpointBuilder> finalConvention) => _conventions.Add(finalConvention);

        public void ApplyTo(EndpointBuilder builder)
        {
            foreach (var convention in _conventions)
            {
                convention(builder);
            }
        }
    }

    /// <summary>The body of <c>GET /test/protected</c>.</summary>
    internal sealed record CallerResponse(bool IsAuthenticated, Guid? UserId, Guid? SessionId, string UICulture, string Culture);
}
