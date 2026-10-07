using Microsoft.AspNetCore.Builder;
using TemplateName.Web.Common.Middleware;

namespace TemplateName.Web.Common;

public static class WebCommonApplicationBuilderExtensions
{
    /// <summary>Sets <c>X-Trace-Id</c> on every response. Register it first so error responses carry it too.</summary>
    public static IApplicationBuilder UseTraceIdHeader(this IApplicationBuilder app)
        => app.UseMiddleware<TraceIdHeaderMiddleware>();

    /// <summary>Sets the baseline security headers on every response.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
        => app.UseMiddleware<SecurityHeadersMiddleware>();
}
