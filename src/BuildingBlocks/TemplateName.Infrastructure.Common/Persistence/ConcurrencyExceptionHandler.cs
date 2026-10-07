using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace TemplateName.Infrastructure.Common.Persistence;

/// <summary>Answers an optimistic-concurrency conflict (<see cref="DbUpdateConcurrencyException"/>) with 409 <c>concurrency.conflict</c>.</summary>
internal sealed partial class ConcurrencyExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ConcurrencyExceptionHandler> logger)
    : IExceptionHandler
{
    private const string ConflictCode = "concurrency.conflict";
    private const string ConflictDetail = "The resource was changed by another request. Reload it and try again.";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not DbUpdateConcurrencyException || httpContext.Response.HasStarted)
        {
            return false;
        }

        LogConcurrencyConflict(logger, exception);
        httpContext.Response.StatusCode = StatusCodes.Status409Conflict;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = StatusCodes.Status409Conflict,
                Detail = ConflictDetail,
                Extensions = { ["code"] = ConflictCode },
            },
        });
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Concurrency conflict while saving changes")]
    private static partial void LogConcurrencyConflict(ILogger logger, Exception exception);
}
