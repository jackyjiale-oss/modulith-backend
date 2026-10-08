using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TemplateName.SharedKernel;

namespace TemplateName.Application.Common.Messaging;

/// <summary>
/// Handler decorators that log each request and its outcome. Exceptions are not caught here:
/// the exception handler logs them once.
/// </summary>
internal static partial class LoggingDecorator
{
    internal sealed class CommandHandler<TCommand>(
        ICommandHandler<TCommand> inner,
        ILogger<CommandHandler<TCommand>> logger)
        : ICommandHandler<TCommand>
    {
        public async Task<Result> HandleAsync(TCommand command, CancellationToken cancellationToken)
        {
            var requestName = typeof(TCommand).Name;
            LogProcessing(logger, requestName);
            var startTimestamp = Stopwatch.GetTimestamp();

            var result = await inner.HandleAsync(command, cancellationToken);

            LogOutcome(logger, requestName, startTimestamp, result);
            return result;
        }
    }

    internal sealed class CommandHandler<TCommand, TResponse>(
        ICommandHandler<TCommand, TResponse> inner,
        ILogger<CommandHandler<TCommand, TResponse>> logger)
        : ICommandHandler<TCommand, TResponse>
    {
        public async Task<Result<TResponse>> HandleAsync(TCommand command, CancellationToken cancellationToken)
        {
            var requestName = typeof(TCommand).Name;
            LogProcessing(logger, requestName);
            var startTimestamp = Stopwatch.GetTimestamp();

            var result = await inner.HandleAsync(command, cancellationToken);

            LogOutcome(logger, requestName, startTimestamp, result);
            return result;
        }
    }

    internal sealed class QueryHandler<TQuery, TResponse>(
        IQueryHandler<TQuery, TResponse> inner,
        ILogger<QueryHandler<TQuery, TResponse>> logger)
        : IQueryHandler<TQuery, TResponse>
    {
        public async Task<Result<TResponse>> HandleAsync(TQuery query, CancellationToken cancellationToken)
        {
            var requestName = typeof(TQuery).Name;
            LogProcessing(logger, requestName);
            var startTimestamp = Stopwatch.GetTimestamp();

            var result = await inner.HandleAsync(query, cancellationToken);

            LogOutcome(logger, requestName, startTimestamp, result);
            return result;
        }
    }

    private static void LogOutcome(ILogger logger, string requestName, long startTimestamp, Result result)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

        if (result.IsSuccess)
        {
            LogCompleted(logger, requestName, elapsedMs);
        }
        else
        {
            LogFailed(logger, requestName, result.Error.Code, elapsedMs);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Processing {RequestName}")]
    private static partial void LogProcessing(ILogger logger, string requestName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Completed {RequestName} in {ElapsedMs} ms")]
    private static partial void LogCompleted(ILogger logger, string requestName, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed {RequestName} with {ErrorCode} in {ElapsedMs} ms")]
    private static partial void LogFailed(ILogger logger, string requestName, string errorCode, double elapsedMs);
}
