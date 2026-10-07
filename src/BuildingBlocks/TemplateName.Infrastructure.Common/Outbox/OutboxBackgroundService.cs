using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TemplateName.Infrastructure.Common.Outbox;

/// <summary>
/// Runs the <typeparamref name="TContext"/> dispatcher while <see cref="OutboxOptions.Enabled"/> is set: it drains the due messages
/// (a full batch is followed immediately by the next), then waits <see cref="OutboxOptions.PollingInterval"/>.
/// </summary>
internal sealed partial class OutboxBackgroundService<TContext>(
    OutboxDispatcher<TContext> dispatcher,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxBackgroundService<TContext>> logger)
    : BackgroundService
    where TContext : DbContext
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var outboxOptions = options.Value;
        if (!outboxOptions.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(outboxOptions.PollingInterval, timeProvider);
        do
        {
            try
            {
                int claimed;
                do
                {
                    claimed = await dispatcher.ProcessBatchAsync(stoppingToken);
                }
                while (claimed == outboxOptions.BatchSize);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                // A database outage must not stop the host; the next poll tries again.
                LogPollFailed(logger, exception, typeof(TContext).Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox poll for {DbContext} failed")]
    private static partial void LogPollFailed(ILogger logger, Exception exception, string dbContext);
}
