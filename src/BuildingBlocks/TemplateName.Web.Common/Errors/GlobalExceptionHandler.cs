using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TemplateName.Web.Common.Errors;

/// <summary>
/// Turns unhandled exceptions into ProblemDetails responses. <c>detail</c> is always the generic message for the code (localized by
/// <c>CustomizeProblemDetails</c>); only in Development does the exception text, with its stack trace, go out in <c>exceptionDetails</c>.
/// </summary>
internal sealed partial class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger,
    IHostEnvironment environment)
    : IExceptionHandler
{
    // English defaults; CustomizeProblemDetails replaces them with the CommonErrorMessages entry for the caller's language.
    private const string UnexpectedErrorDetail = "An unexpected error occurred.";
    private const string MalformedRequestDetail = "The request is malformed.";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (httpContext.Response.HasStarted)
        {
            return false;
        }

        int status;
        string code;
        string detail;
        string? exceptionDetails = null;

        if (exception is BadHttpRequestException badRequest)
        {
            status = badRequest.StatusCode;
            code = "request.malformed";
            detail = MalformedRequestDetail;
            LogMalformedRequest(logger, badRequest.Message);
        }
        else
        {
            LogUnhandledException(logger, exception);
            status = StatusCodes.Status500InternalServerError;
            code = "server.unexpected_error";
            detail = UnexpectedErrorDetail;
            exceptionDetails = environment.IsDevelopment() ? exception.ToString() : null;
        }

        httpContext.Response.StatusCode = status;
        var context = new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Detail = detail,
                Extensions = { ["code"] = code },
            },
        };

        if (exceptionDetails is not null)
        {
            context.ProblemDetails.Extensions["exceptionDetails"] = exceptionDetails;
        }

        return await problemDetailsService.TryWriteAsync(context);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Malformed request: {Reason}")]
    private static partial void LogMalformedRequest(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception")]
    private static partial void LogUnhandledException(ILogger logger, Exception exception);
}
