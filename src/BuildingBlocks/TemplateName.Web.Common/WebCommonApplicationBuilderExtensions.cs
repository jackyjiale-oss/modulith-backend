using Microsoft.AspNetCore.Builder;
using TemplateName.Web.Common.Middleware;

namespace TemplateName.Web.Common;

public static class WebCommonApplicationBuilderExtensions
{
    /// <summary>Adds <c>X-Trace-Id</c> to every response when it starts, including error responses. Register it before the exception handler.</summary>
    public static IApplicationBuilder UseTraceIdHeader(this IApplicationBuilder app)
        => app.UseMiddleware<TraceIdHeaderMiddleware>();

    /// <summary>Adds the baseline security headers to every response when it starts, including error responses. Register it before the exception handler.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
        => app.UseMiddleware<SecurityHeadersMiddleware>();
}
