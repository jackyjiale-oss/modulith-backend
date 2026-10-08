using Microsoft.AspNetCore.Http;

namespace TemplateName.Web.Common.Middleware;

/// <summary>
/// Adds the baseline security headers for a JSON API. They are registered with <c>OnStarting</c> before the pipeline runs,
/// so they are written when the response starts and survive the exception handler clearing the response headers.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(
            static state =>
            {
                var headers = ((HttpContext)state).Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers["X-Frame-Options"] = "DENY";
                headers["Content-Security-Policy"] = "frame-ancestors 'none'";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
                return Task.CompletedTask;
            },
            context);

        return next(context);
    }
}
