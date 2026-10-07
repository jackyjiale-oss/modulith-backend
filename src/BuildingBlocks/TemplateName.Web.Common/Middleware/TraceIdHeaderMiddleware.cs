using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace TemplateName.Web.Common.Middleware;

/// <summary>Echoes the W3C trace id as <c>X-Trace-Id</c>, set before the pipeline runs so every response (including errors) carries it.</summary>
internal sealed class TraceIdHeaderMiddleware(RequestDelegate next)
{
    internal const string HeaderName = "X-Trace-Id";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers[HeaderName] = ResolveTraceId(context);
        return next(context);
    }

    internal static string ResolveTraceId(HttpContext context)
        => Activity.Current?.TraceId.ToHexString() ?? context.TraceIdentifier;
}
