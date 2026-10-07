using Microsoft.AspNetCore.Builder;
using TemplateName.Web.Common.Middleware;

namespace TemplateName.Web.Common;

public static class WebCommonApplicationBuilderExtensions
{
    /// <summary>Adds <c>X-Trace-Id</c> to every response when it starts, including error responses. It is written through <c>Response.OnStarting</c>, so registering it after the exception handler works too; middleware placed ahead of it that short-circuits will not receive the header.</summary>
    public static IApplicationBuilder UseTraceIdHeader(this IApplicationBuilder app)
        => app.UseMiddleware<TraceIdHeaderMiddleware>();

    /// <summary>Adds the baseline security headers to every response when it starts, including error responses. They are written through <c>Response.OnStarting</c>, so registering it after the exception handler works too; middleware placed ahead of it that short-circuits will not receive the headers.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
        => app.UseMiddleware<SecurityHeadersMiddleware>();
}
