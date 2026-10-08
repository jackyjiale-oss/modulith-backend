using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// The test server has no remote address. A request with <see cref="HeaderName"/> gets that address as its connection's remote address,
/// set ahead of the whole pipeline, so the forwarded-headers middleware, the per-address rate limit and the client context see it as
/// they would see a real client. Requests without the header are untouched.
/// </summary>
internal sealed class TestClientAddressStartupFilter : IStartupFilter
{
    internal const string HeaderName = "X-Test-Client-Address";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(HeaderName, out var value) && IPAddress.TryParse(value.ToString(), out var address))
            {
                context.Connection.RemoteIpAddress = address;
            }

            return nextMiddleware(context);
        });
        next(app);
    };
}
