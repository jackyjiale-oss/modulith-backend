using Microsoft.AspNetCore.Http;
using TemplateName.Web.Common.Middleware;

namespace TemplateName.Web.Common.Errors;

internal static class ProblemDetailsSetup
{
    /// <summary>Adds <c>traceId</c> and, when missing, a generic <c>code</c> to every problem response.</summary>
    internal static void Customize(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        problem.Extensions["traceId"] = TraceIdHeaderMiddleware.ResolveTraceId(context.HttpContext);

        if (!problem.Extensions.ContainsKey("code"))
        {
            var status = problem.Status ?? context.HttpContext.Response.StatusCode;
            problem.Extensions["code"] = $"http.{status}";
        }
    }
}
