using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TemplateName.Modules.Notifications.Infrastructure.Deliveries;

/// <summary>
/// Runs the <see cref="DeliveryDispatcher"/> while <see cref="DeliveryOptions.Enabled"/> is set, like the outbox's background service:
/// it drains the due deliveries (a full batch is followed immediately by the next), then waits
/// <see cref="DeliveryOptions.PollingInterval"/> on the <see cref="TimeProvider"/>. A failed poll is logged and the next one tries again.
/// On shutdown it stops between deliveries; what it had claimed but not settled is re-claimed once the lease runs out.
/// </summary>
internal sealed partial class DeliveryBackgroundService(
    DeliveryDispatcher dispatcher,
    IOptions<DeliveryOptions> options,
    TimeProvider timeProvider,
    ILogger<DeliveryBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var deliveryOptions = options.Value;
        if (!deliveryOptions.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(deliveryOptions.PollingInterval, timeProvider);
        do
        {
            try
            {
                int claimed;
                do
                {
                    claimed = await dispatcher.ProcessBatchAsync(cancellationToken);
                }
                while (claimed == deliveryOptions.BatchSize);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A database outage must not stop the host; the next poll tries again.
                LogPollFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Notification delivery poll failed")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);
}
