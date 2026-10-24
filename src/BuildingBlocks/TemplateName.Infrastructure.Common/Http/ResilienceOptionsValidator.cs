using Microsoft.Extensions.Options;

namespace TemplateName.Infrastructure.Common.Http;

/// <summary>Checks every named <see cref="ResilienceOptions"/>; the messages name the configuration key at fault.</summary>
internal sealed class ResilienceOptionsValidator : IValidateOptions<ResilienceOptions>
{
    private const int MaxRetries = 10;
    private const int MinimumBreakerThroughput = 2;
    private static readonly TimeSpan MinimumBreakerWindow = TimeSpan.FromMilliseconds(500);

    public ValidateOptionsResult Validate(string? name, ResilienceOptions options)
    {
        var prefix = $"{ResilienceOptions.SectionName}:{name}:";
        var failures = new List<string>();

        if (options.TotalTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{prefix}{nameof(ResilienceOptions.TotalTimeout)} must be a positive time span.");
        }

        if (options.AttemptTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{prefix}{nameof(ResilienceOptions.AttemptTimeout)} must be a positive time span.");
        }
        else if (options.AttemptTimeout > options.TotalTimeout)
        {
            failures.Add($"{prefix}{nameof(ResilienceOptions.AttemptTimeout)} must not be above {nameof(ResilienceOptions.TotalTimeout)}.");
        }

        if (options.Retries is < 0 or > MaxRetries)
        {
            failures.Add($"{prefix}{nameof(ResilienceOptions.Retries)} must be from 0 to {MaxRetries}.");
        }

        if (options.BaseDelay <= TimeSpan.Zero)
        {
            failures.Add($"{prefix}{nameof(ResilienceOptions.BaseDelay)} must be a positive time span.");
        }

        ValidateBreaker($"{prefix}{nameof(ResilienceOptions.Breaker)}:", options.Breaker, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateBreaker(string prefix, BreakerOptions breaker, List<string> failures)
    {
        // The upper bound is inclusive (a ratio of 1 opens the breaker only when every attempt failed); NaN fails both comparisons.
        if (!(breaker.FailureRatio > 0 && breaker.FailureRatio <= 1))
        {
            failures.Add($"{prefix}{nameof(BreakerOptions.FailureRatio)} must be above 0 and at most 1.");
        }

        if (breaker.MinimumThroughput < MinimumBreakerThroughput)
        {
            failures.Add($"{prefix}{nameof(BreakerOptions.MinimumThroughput)} must be at least {MinimumBreakerThroughput}.");
        }

        if (breaker.SamplingDuration < MinimumBreakerWindow)
        {
            failures.Add($"{prefix}{nameof(BreakerOptions.SamplingDuration)} must be at least {MinimumBreakerWindow}.");
        }

        if (breaker.BreakDuration < MinimumBreakerWindow)
        {
            failures.Add($"{prefix}{nameof(BreakerOptions.BreakDuration)} must be at least {MinimumBreakerWindow}.");
        }
    }
}
