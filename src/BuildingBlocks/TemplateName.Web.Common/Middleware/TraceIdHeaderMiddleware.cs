using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace TemplateName.Web.Common.Middleware;

/// <summary>
/// Echoes the W3C trace id as <c>X-Trace-Id</c>. The header is registered with <c>OnStarting</c> before the pipeline runs,
/// so it is written when the response starts and survives the exception handler clearing the response headers.
/// </summary>
internal sealed class TraceIdHeaderMiddleware(RequestDelegate next)
{
    internal const string HeaderName = "X-Trace-Id";

    public Task InvokeAsync(HttpContext context)
    {
        var traceId = ResolveTraceId(context);
        context.Response.OnStarting(
            static state =>
            {
                var (httpContext, id) = ((HttpContext, string))state;
                httpContext.Response.Headers[HeaderName] = id;
                return Task.CompletedTask;
            },
            (context, traceId));

        return next(context);
    }

    internal static string ResolveTraceId(HttpContext context)
        => Activity.Current?.TraceId.ToHexString() ?? context.TraceIdentifier;
}
