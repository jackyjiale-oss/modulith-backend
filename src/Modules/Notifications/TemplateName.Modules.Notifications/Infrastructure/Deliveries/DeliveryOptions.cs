using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Notifications.Infrastructure.Deliveries;

/// <summary>The delivery worker (section <c>Notifications:Delivery</c>), validated on start.</summary>
internal sealed class DeliveryOptions
{
    internal const string SectionName = "Notifications:Delivery";

    /// <summary>Runs the background worker. Tests turn it off and call <c>DeliveryDispatcher.ProcessBatchAsync</c> themselves.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The pause between polls once the due deliveries are drained.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The most deliveries one worker claims per batch.</summary>
    [Range(1, 100)]
    public int BatchSize { get; set; } = 10;

    /// <summary>The attempts a delivery gets before it is dead-lettered (blueprint 10.4: five).</summary>
    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// How long a claimed batch stays hidden from other workers. Keep it longer than the slowest batch takes (a batch of slow SMTP
    /// sends): a worker stops starting deliveries once less than <see cref="LeaseSafetyMargin"/> is left and releases the rest of the
    /// batch, which the next poll claims again.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How much of the lease must be left for the worker to start another delivery: a fifth of <see cref="LeaseDuration"/>, at least
    /// 30 seconds but never more than half the lease (one minute with the default lease). A delivery started closer to the end could
    /// still be sending when the lease runs out and another worker re-claims it; the worker releases the rest of its batch instead.
    /// </summary>
    public TimeSpan LeaseSafetyMargin
    {
        get
        {
            var margin = LeaseDuration / 5;
            if (margin < MinLeaseSafetyMargin)
            {
                margin = MinLeaseSafetyMargin;
            }

            return margin > LeaseDuration / 2 ? LeaseDuration / 2 : margin;
        }
    }

    private static TimeSpan MinLeaseSafetyMargin => TimeSpan.FromSeconds(30);
}
