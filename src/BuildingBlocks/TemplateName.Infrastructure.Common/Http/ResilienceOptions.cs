namespace TemplateName.Infrastructure.Common.Http;

/// <summary>
/// One resilience pipeline's settings (section <c>Resilience:Pipelines:{pipeline}</c>), read as the named options of the pipeline's
/// name. The initial values are those of <see cref="ResiliencePipelines.Default"/>; <see cref="ResilienceOptionsSetup"/> starts the
/// other built-in pipelines from their own values and then applies the section. Validated on start by
/// <see cref="ResilienceOptionsValidator"/>.
/// </summary>
internal sealed class ResilienceOptions
{
    internal const string SectionName = "Resilience:Pipelines";

    /// <summary>The time all attempts, the delays between them and the breaker wait may take together.</summary>
    public TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The time one attempt may take; an attempt that runs longer is cancelled and counts as a failure.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How many times a failed call is repeated after the first attempt; 0 turns retrying off.</summary>
    public int Retries { get; set; } = 3;

    /// <summary>Retries a POST, PUT, PATCH, DELETE or CONNECT as well; off by default because a blind repeat can do the work twice.</summary>
    public bool RetryUnsafeMethods { get; set; }

    /// <summary>The first retry delay; later delays grow exponentially, with jitter.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>The circuit breaker thresholds.</summary>
    public BreakerOptions Breaker { get; set; } = new();
}
