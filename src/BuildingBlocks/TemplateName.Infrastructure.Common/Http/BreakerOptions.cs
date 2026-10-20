namespace TemplateName.Infrastructure.Common.Http;

/// <summary>The circuit breaker of a pipeline (section <c>Resilience:Pipelines:{pipeline}:Breaker</c>).</summary>
internal sealed class BreakerOptions
{
    /// <summary>The share of failed attempts in the sampling window at which the breaker opens.</summary>
    public double FailureRatio { get; set; } = 0.5;

    /// <summary>The fewest attempts the sampling window must hold before the ratio is looked at.</summary>
    public int MinimumThroughput { get; set; } = 10;

    /// <summary>The window the ratio is measured over.</summary>
    public TimeSpan SamplingDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long an open breaker rejects calls before it lets one probe through (half-open).</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}
