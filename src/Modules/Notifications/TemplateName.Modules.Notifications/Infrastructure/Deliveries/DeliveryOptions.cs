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
    /// sends): a worker stops starting deliveries once it has run out, and another worker re-claims what is left.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
}
