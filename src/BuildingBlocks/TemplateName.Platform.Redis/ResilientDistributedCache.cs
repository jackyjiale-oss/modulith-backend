using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;

namespace TemplateName.Platform.Redis;

/// <summary>
/// The Redis second level of <c>HybridCache</c>, made safe to lose (spec 3.3): every call goes through <see cref="RedisAvailability"/>.
/// A failing call never reaches the caller: a read answers a miss (so the value is loaded from its source), and a write, refresh or
/// remove is dropped. An asynchronous call that has not finished after <see cref="RedisCachingOptions.CommandTimeout"/> (measured with
/// the application's <see cref="TimeProvider"/>) is given up and counts as a failure, so a Redis that hangs costs a request at most that
/// long. While the circuit is open the inner cache is not called at all, and after the break only its one probe call is. Only the
/// caller's own cancellation propagates (an <see cref="OperationCanceledException"/> for a cancelled <c>cancellationToken</c>), and it is
/// not counted as a Redis failure. The synchronous methods rely on the connection's own timeouts (nothing in the template calls them).
/// <para>
/// It is deliberately not <see cref="IDisposable"/> and never disposes the inner <c>RedisCache</c>: <c>RedisCache.Dispose</c> closes and
/// disposes the multiplexer it got from <c>ConnectionMultiplexerFactory</c>, which is the instance's one shared connection (the readiness
/// check uses it too). The container created that connection and is its only owner; it disposes it when the host stops. The inner
/// cache holds nothing else worth releasing at shutdown.
/// </para>
/// </summary>
internal sealed class ResilientDistributedCache(
    IDistributedCache inner,
    RedisAvailability availability,
    TimeProvider timeProvider,
    IOptions<RedisCachingOptions> options) : IDistributedCache
{
    private readonly TimeSpan _commandTimeout = options.Value.CommandTimeout;

    public byte[]? Get(string key) => Run(() => inner.Get(key));

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
        => RunAsync(() => inner.GetAsync(key, cancellationToken), cancellationToken);

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => Run(() =>
    {
        inner.Set(key, value, options);
        return true;
    });

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken cancellationToken = default)
        => RunAsync(() => inner.SetAsync(key, value, options, cancellationToken), cancellationToken);

    public void Refresh(string key) => Run(() =>
    {
        inner.Refresh(key);
        return true;
    });

    public Task RefreshAsync(string key, CancellationToken cancellationToken = default)
        => RunAsync(() => inner.RefreshAsync(key, cancellationToken), cancellationToken);

    public void Remove(string key) => Run(() =>
    {
        inner.Remove(key);
        return true;
    });

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => RunAsync(() => inner.RemoveAsync(key, cancellationToken), cancellationToken);

    private T? Run<T>(Func<T> call)
    {
        if (!availability.TryBeginCall())
        {
            return default;
        }

        try
        {
            var result = call();
            availability.RecordSuccess();
            return result;
        }
        catch (Exception exception)
        {
            availability.RecordFailure(exception);
            return default;
        }
    }

    private async Task<byte[]?> RunAsync(Func<Task<byte[]?>> call, CancellationToken cancellationToken)
    {
        if (!availability.TryBeginCall())
        {
            return null;
        }

        try
        {
            var result = await call().WaitAsync(_commandTimeout, timeProvider, cancellationToken);
            availability.RecordSuccess();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            availability.AbandonCall();
            throw;
        }
        catch (Exception exception)
        {
            availability.RecordFailure(exception);
            return null;
        }
    }

    private async Task RunAsync(Func<Task> call, CancellationToken cancellationToken)
    {
        if (!availability.TryBeginCall())
        {
            return;
        }

        try
        {
            await call().WaitAsync(_commandTimeout, timeProvider, cancellationToken);
            availability.RecordSuccess();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            availability.AbandonCall();
            throw;
        }
        catch (Exception exception)
        {
            availability.RecordFailure(exception);
        }
    }
}
