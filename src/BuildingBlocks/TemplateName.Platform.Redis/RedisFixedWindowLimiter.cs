using System.Threading.RateLimiting;
using StackExchange.Redis;

namespace TemplateName.Platform.Redis;

/// <summary>
/// The fixed window of one partition of a rate-limit policy, counted in Redis so every instance shares it (D5, ADR 0027). Each
/// <see cref="RateLimiter.AcquireAsync(int, CancellationToken)"/> runs <see cref="Script"/> once on the key
/// <c>{InstanceName}rl:{policy}:{partitionKey}</c>: the count goes up by the permits asked for (1 for an HTTP request), the key gets the
/// window as its expiry when the count is new or when the key has none (so a key that lost its expiry is repaired), and the script
/// answers the count and the key's remaining time in milliseconds. The request is allowed while the count is within the limit; a refusal
/// carries <see cref="MetadataName.RetryAfter"/> of that remaining time, at least one second. Requests are never queued.
/// <para>
/// Redis is optional (spec section 4). Every call goes through the shared <see cref="RedisAvailability"/> circuit, exactly like the
/// cache: a call that fails, or has not answered after <see cref="RedisCachingOptions.CommandTimeout"/> (measured with the application's
/// <see cref="TimeProvider"/>), is recorded as a failure, and while the circuit is open (or another caller holds the half-open probe)
/// Redis is not called. In both cases the request is counted by an in-memory <see cref="FixedWindowRateLimiter"/> of the same limit and
/// window, owned by this limiter, so while Redis is down each instance limits on its own (the effective limit is instances × limit). Only
/// the caller's own cancellation propagates; it is neither a Redis failure nor counted in memory, and frees a probe it held.
/// </para>
/// <para>
/// <see cref="RateLimiter.AttemptAcquire(int)"/> cannot wait for Redis. While the circuit is in its break it answers from the in-memory
/// window, as an asynchronous acquire would. Otherwise it refuses without counting anything and without <c>Retry-After</c>: the
/// ASP.NET Core rate-limiting middleware tries <c>AttemptAcquire</c> first and calls <c>AcquireAsync</c> only after a refusal, so an
/// answer from memory here would keep every request away from Redis.
/// </para>
/// <para>
/// The partitioned limiter of ASP.NET Core keeps one instance per partition key and disposes it once <see cref="IdleDuration"/> passes
/// its idle limit, so creating one is cheap: it holds the <see cref="IDatabase"/> handle of the shared connection (never closing or
/// disposing that connection) and creates its in-memory window only when it first needs it. It is not idle while a call is in flight or
/// while its in-memory window holds permits, so a partition is never dropped with a count that only this instance knows.
/// </para>
/// </summary>
internal sealed class RedisFixedWindowLimiter : RateLimiter
{
    /// <summary>
    /// KEYS[1] is the key, ARGV[1] the permits asked for, ARGV[2] the window in milliseconds. <c>PTTL</c> is -1 for a key without an
    /// expiry. Keys and values are always passed as arguments, never written into the script, so the script (and its cached hash) is the
    /// same for every key, and no partition key (<c>ip:::1</c>, <c>user:{sub}</c>) can change what it does.
    /// </summary>
    internal const string Script = """
        local count = redis.call('INCRBY', KEYS[1], ARGV[1])
        local ttl = redis.call('PTTL', KEYS[1])
        if count == tonumber(ARGV[1]) or ttl < 0 then
            redis.call('PEXPIRE', KEYS[1], ARGV[2])
            ttl = tonumber(ARGV[2])
        end
        return { count, ttl }
        """;

    private static readonly TimeSpan MinimumRetryAfter = TimeSpan.FromSeconds(1);
    private static readonly RateLimitLease Acquired = new FixedWindowLease(isAcquired: true, retryAfter: null);

    // A refusal that only means "ask again asynchronously": no Retry-After, nothing counted.
    private static readonly RateLimitLease NotAcquiredSynchronously = new FixedWindowLease(isAcquired: false, retryAfter: null);

    private readonly IDatabase _database;
    private readonly RedisKey[] _keys;
    private readonly int _permitLimit;
    private readonly TimeSpan _window;
    private readonly RedisValue _windowMilliseconds;
    private readonly RedisAvailability _availability;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _commandTimeout;
    private readonly Lock _lock = new();
    private FixedWindowRateLimiter? _fallback;
    private volatile bool _disposed;
    private int _callsInFlight;
    private long _lastActivity;
    private long _availablePermits;
    private long _successfulLeases;
    private long _failedLeases;

    internal RedisFixedWindowLimiter(
        IDatabase database,
        RedisKey key,
        int permitLimit,
        TimeSpan window,
        RedisAvailability availability,
        TimeProvider timeProvider,
        TimeSpan commandTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(permitLimit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(window, TimeSpan.FromMilliseconds(1));

        _database = database;
        _keys = [key];
        _permitLimit = permitLimit;
        _window = window;
        _windowMilliseconds = (long)Math.Ceiling(window.TotalMilliseconds);
        _availability = availability;
        _timeProvider = timeProvider;
        _commandTimeout = commandTimeout;
        _lastActivity = timeProvider.GetTimestamp();
        _availablePermits = permitLimit;
    }

    /// <summary>
    /// The time since the last acquire ended, or null while one is in flight or while the in-memory window holds permits (the partition
    /// must not be dropped then). Measured with the application's <see cref="TimeProvider"/>.
    /// </summary>
    public override TimeSpan? IdleDuration
    {
        get
        {
            if (Volatile.Read(ref _callsInFlight) > 0)
            {
                return null;
            }

            FixedWindowRateLimiter? fallback;
            lock (_lock)
            {
                fallback = _fallback;
            }

            return fallback is not null && fallback.IdleDuration is null
                ? null
                : _timeProvider.GetElapsedTime(Interlocked.Read(ref _lastActivity));
        }
    }

    /// <summary>
    /// The leases this limiter answered (Redis and in memory; not the deferrals of <see cref="RateLimiter.AttemptAcquire(int)"/>), and the
    /// permits left as of Redis's last answer.
    /// </summary>
    public override RateLimiterStatistics GetStatistics() => new()
    {
        CurrentAvailablePermits = Interlocked.Read(ref _availablePermits),
        CurrentQueuedCount = 0,
        TotalSuccessfulLeases = Interlocked.Read(ref _successfulLeases),
        TotalFailedLeases = Interlocked.Read(ref _failedLeases),
    };

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ValidatePermitCount(permitCount);
        Interlocked.Exchange(ref _lastActivity, _timeProvider.GetTimestamp());

        return _availability.IsInBreak ? AcquireInMemory(permitCount) : NotAcquiredSynchronously;
    }

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        ValidatePermitCount(permitCount);
        cancellationToken.ThrowIfCancellationRequested();

        Interlocked.Increment(ref _callsInFlight);
        try
        {
            if (!_availability.TryBeginCall())
            {
                return AcquireInMemory(permitCount);
            }

            RateLimitLease lease;
            try
            {
                var result = await _database.ScriptEvaluateAsync(Script, _keys, [permitCount, _windowMilliseconds])
                    .WaitAsync(_commandTimeout, _timeProvider, cancellationToken);
                lease = Decide(permitCount, result);
                _availability.RecordSuccess();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _availability.AbandonCall();
                throw;
            }
            catch (Exception exception)
            {
                _availability.RecordFailure(exception);
                return AcquireInMemory(permitCount);
            }

            return Counted(lease);
        }
        finally
        {
            Interlocked.Exchange(ref _lastActivity, _timeProvider.GetTimestamp());
            Interlocked.Decrement(ref _callsInFlight);
        }
    }

    protected override void Dispose(bool disposing)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _fallback?.Dispose();
        }
    }

    protected override ValueTask DisposeAsyncCore()
    {
        Dispose(disposing: true);
        return default;
    }

    private RateLimitLease Decide(int permitCount, RedisResult result)
    {
        var reply = (RedisResult[]?)result;
        if (reply is not { Length: 2 })
        {
            throw new InvalidOperationException("The rate-limit script answered something other than a count and a time to live.");
        }

        var count = (long)reply[0];
        var timeToLive = TimeSpan.FromMilliseconds((long)reply[1]);
        Interlocked.Exchange(ref _availablePermits, Math.Max(0, _permitLimit - count));

        // Asking for no permit asks whether any is left.
        var isAcquired = permitCount == 0 ? count < _permitLimit : count <= _permitLimit;
        return isAcquired ? Acquired : new FixedWindowLease(isAcquired: false, timeToLive > MinimumRetryAfter ? timeToLive : MinimumRetryAfter);
    }

    private RateLimitLease AcquireInMemory(int permitCount)
    {
        FixedWindowRateLimiter fallback;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            fallback = _fallback ??= new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
            {
                PermitLimit = _permitLimit,
                Window = _window,
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        }

        return Counted(fallback.AttemptAcquire(permitCount));
    }

    private RateLimitLease Counted(RateLimitLease lease)
    {
        Interlocked.Increment(ref lease.IsAcquired ? ref _successfulLeases : ref _failedLeases);
        return lease;
    }

    private void ValidatePermitCount(int permitCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(permitCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(permitCount, _permitLimit);
    }

    private sealed class FixedWindowLease(bool isAcquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => isAcquired;

        public override IEnumerable<string> MetadataNames => retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is { } value && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = value;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
