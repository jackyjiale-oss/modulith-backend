using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TemplateName.Platform.Redis;

/// <summary>
/// The circuit breaker every Redis caller of this instance shares (one singleton). Callers ask <see cref="TryBeginCall"/> before a Redis
/// call and record its outcome. After <see cref="RedisCachingOptions.FailureThreshold"/> consecutive failures the circuit is open for
/// <see cref="RedisCachingOptions.BreakDuration"/>: callers skip Redis (a cache read is a miss, a rate limit is counted in memory)
/// instead of waiting for it on every request. When the break is over the circuit is half-open: exactly one call (the probe) goes to
/// Redis while every other caller still skips it; the probe's success closes the circuit, its failure opens it for another break. One
/// warning is logged per outage window (from the circuit opening until the next success), naming the exception's type but never its
/// message, and one information line when Redis answers again. Time is the application's <see cref="TimeProvider"/>. Thread-safe.
/// </summary>
public sealed partial class RedisAvailability(TimeProvider timeProvider, IOptions<RedisCachingOptions> options, ILogger<RedisAvailability> logger)
{
    private readonly Lock _lock = new();
    private readonly RedisCachingOptions _options = options.Value;
    private int _consecutiveFailures;
    private DateTimeOffset? _openUntil;
    private bool _isInOutage;
    private bool _probeInFlight;

    /// <summary>
    /// True when the circuit is closed, or its break is over and no probe is running (the next call may go to Redis). A read only: it
    /// does not claim the probe.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            lock (_lock)
            {
                return _openUntil is not { } openUntil || (timeProvider.GetUtcNow() >= openUntil && !_probeInFlight);
            }
        }
    }

    /// <summary>True while the circuit is open and its break is not over: nobody calls Redis now, not even a probe.</summary>
    internal bool IsInBreak
    {
        get
        {
            lock (_lock)
            {
                return _openUntil is { } openUntil && timeProvider.GetUtcNow() < openUntil;
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
            _probeInFlight = false;
        }

        if (outageEnded)
        {
            LogAvailableAgain(logger);
        }
    }

    /// <summary>
    /// Records a Redis call that failed. At the threshold (and at every failure after it, until a success) the circuit opens for a
    /// break; the first opening of an outage logs the warning, with the type of <paramref name="exception"/> only: a Redis exception's
    /// message and data can name the key, and a key can hold a client address or a user id.
    /// </summary>
    public void RecordFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        bool outageStarted;
        int failures;
        lock (_lock)
        {
            _probeInFlight = false;
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
            LogUnavailable(logger, failures, _options.BreakDuration, exception.GetType().Name);
        }
    }

    /// <summary>
    /// Whether the caller may call Redis now: always while the circuit is closed, never during a break, and after the break for the one
    /// caller that becomes the probe. A caller that gets true reports with <see cref="RecordSuccess"/>, <see cref="RecordFailure"/> or,
    /// when it gave up without an outcome (its own cancellation), <see cref="AbandonCall"/>.
    /// </summary>
    internal bool TryBeginCall()
    {
        lock (_lock)
        {
            if (_openUntil is not { } openUntil)
            {
                return true;
            }

            if (timeProvider.GetUtcNow() < openUntil || _probeInFlight)
            {
                return false;
            }

            _probeInFlight = true;
            return true;
        }
    }

    /// <summary>Ends a call that has no outcome (cancelled by its own caller): a probe it held is free for the next caller.</summary>
    internal void AbandonCall()
    {
        lock (_lock)
        {
            _probeInFlight = false;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Redis failed {FailureCount} times in a row ({ExceptionType}) and is skipped for {BreakDuration} at a time until it answers again; cache reads go to the database and rate limits are counted in memory by each instance meanwhile")]
    private static partial void LogUnavailable(ILogger logger, int failureCount, TimeSpan breakDuration, string exceptionType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Redis answers again; the cache and the rate limiters use it")]
    private static partial void LogAvailableAgain(ILogger logger);
}
