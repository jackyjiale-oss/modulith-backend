using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TemplateName.Infrastructure.Common.Jobs;

/// <summary>
/// Runs the due recurring jobs every <see cref="JobsOptions.TickInterval"/> while <see cref="JobsOptions.Enabled"/> is set; does nothing
/// when it is not. A failed tick (a database outage) is logged and the next tick tries again.
/// </summary>
internal sealed partial class RecurringJobService(
    RecurringJobRunner runner,
    IOptions<JobsOptions> options,
    TimeProvider timeProvider,
    ILogger<RecurringJobService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var jobsOptions = options.Value;
        if (!jobsOptions.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(jobsOptions.TickInterval, timeProvider);
        do
        {
            try
            {
                await runner.RunDueAsync(include: null, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A database outage must not stop the host; the next tick tries again.
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Recurring job tick failed")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
