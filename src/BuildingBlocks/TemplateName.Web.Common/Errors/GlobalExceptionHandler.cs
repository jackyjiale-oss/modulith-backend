using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TemplateName.Web.Common.Errors;

/// <summary>Turns unhandled exceptions into ProblemDetails responses; stack traces leave the process only in Development.</summary>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger,
    IHostEnvironment environment)
    : IExceptionHandler
{
    private const string UnexpectedErrorDetail = "An unexpected error occurred.";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        int status;
        string code;
        string detail;

        if (exception is BadHttpRequestException badRequest)
        {
            status = badRequest.StatusCode;
            code = "request.malformed";
            detail = badRequest.Message;
        }
        else
        {
            LogUnhandledException(logger, exception);
            status = StatusCodes.Status500InternalServerError;
            code = "server.unexpected_error";
            detail = environment.IsDevelopment() ? exception.ToString() : UnexpectedErrorDetail;
        }

        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Detail = detail,
                Extensions = { ["code"] = code },
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception);
}
