using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TemplateName.Platform.Redis;

/// <summary>
/// The circuit breaker every Redis caller of this instance shares (one singleton): callers record each Redis call's outcome, and after
/// <see cref="RedisCachingOptions.FailureThreshold"/> consecutive failures <see cref="IsAvailable"/> is false for
/// <see cref="RedisCachingOptions.BreakDuration"/>, so callers skip Redis (a cache read is a miss) instead of waiting for its timeout on
/// every request. When the break is over, calls go to Redis again: a success closes the circuit, a failure opens it for another break.
/// One warning is logged per outage window (from the circuit opening until the next success) and one information line when Redis
/// answers again. Time is the application's <see cref="TimeProvider"/>. Thread-safe.
/// </summary>
public sealed partial class RedisAvailability(TimeProvider timeProvider, IOptions<RedisCachingOptions> options, ILogger<RedisAvailability> logger)
{
    private readonly Lock _lock = new();
    private readonly RedisCachingOptions _options = options.Value;
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;
    private bool _isInOutage;

    /// <summary>False while the circuit is open: skip Redis. True when it is closed or its break is over.</summary>
    public bool IsAvailable
    {
        get
        {
            lock (_lock)
            {
                return _openUntil is not { } openUntil || timeProvider.GetUtcNow() >= openUntil;
            }
        }
    }

    /// <summary>Records a Redis call that succeeded: the failure count starts again, and an outage, if there was one, is over.</summary>
    public void RecordSuccess()
    {
        bool outageEnded;
        lock (_lock)
        {
            outageEnded = _isInOutage;
            _consecutiveFailures = 0;
            _openUntil = null;
            _isInOutage = false;
        }

        if (outageEnded)
        {
            LogAvailableAgain(logger);
        }
    }

    /// <summary>
    /// Records a Redis call that failed. At the threshold (and at every failure after it, until a success) the circuit opens for a
    /// break; the first opening of an outage logs the warning, with <paramref name="exception"/>.
    /// </summary>
    public void RecordFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        bool outageStarted;
        int failures;
        lock (_lock)
        {
            failures = ++_consecutiveFailures;
            if (failures < _options.FailureThreshold)
            {
                return;
            }

            _openUntil = timeProvider.GetUtcNow() + _options.BreakDuration;
            outageStarted = !_isInOutage;
            _isInOutage = true;
        }

        if (outageStarted)
        {
            LogUnavailable(logger, failures, _options.BreakDuration, exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Redis failed {FailureCount} times in a row and is skipped for {BreakDuration} at a time until it answers again; cache reads go to the database meanwhile")]
    private static partial void LogUnavailable(ILogger logger, int failureCount, TimeSpan breakDuration, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Redis answers again; the cache uses it")]
    private static partial void LogAvailableAgain(ILogger logger);
}
