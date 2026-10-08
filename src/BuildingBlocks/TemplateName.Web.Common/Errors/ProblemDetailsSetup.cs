using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Localization;
using TemplateName.Web.Common.Middleware;

namespace TemplateName.Web.Common.Errors;

internal static class ProblemDetailsSetup
{
    /// <summary>
    /// Adds <c>traceId</c> and, when missing, a generic <c>code</c> to every problem response, and replaces <c>detail</c> with the
    /// message for <c>code</c> (filled from <c>params</c>) in the request's UI culture. Without a message the English detail (or the
    /// title) stays. This is the only place error messages are localized (ADR 0009).
    /// </summary>
    internal static void Customize(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        problem.Extensions["traceId"] = TraceIdHeaderMiddleware.ResolveTraceId(context.HttpContext);

        if (!problem.Extensions.TryGetValue("code", out var value) || value is not string code)
        {
            var status = problem.Status ?? context.HttpContext.Response.StatusCode;
            code = $"http.{status}";
            problem.Extensions["code"] = code;
        }

        var parameters = problem.Extensions.TryGetValue("params", out var values) ? values as IReadOnlyDictionary<string, object?> : null;
        var localizer = context.HttpContext.RequestServices.GetRequiredService<IErrorMessageLocalizer>();
        problem.Detail = localizer.Localize(code, parameters, fallback: problem.Detail ?? problem.Title ?? string.Empty);
    }
}
