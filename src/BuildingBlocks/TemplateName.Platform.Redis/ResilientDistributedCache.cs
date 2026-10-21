using Microsoft.Extensions.Caching.Distributed;

namespace TemplateName.Platform.Redis;

/// <summary>
/// The Redis second level of <c>HybridCache</c>, made safe to lose (spec 3.3): every call goes through <see cref="RedisAvailability"/>.
/// A failing call never reaches the caller: a read answers a miss (so the value is loaded from its source), and a write, refresh or
/// remove is dropped. While the circuit is open the inner cache is not called at all. Only the caller's own cancellation propagates
/// (an <see cref="OperationCanceledException"/> for a cancelled <c>cancellationToken</c>), and it is not counted as a Redis failure.
/// </summary>
internal sealed class ResilientDistributedCache(IDistributedCache inner, RedisAvailability availability) : IDistributedCache, IDisposable
{
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

    /// <summary>The inner cache is created by this decorator's factory, so the container does not dispose it; the decorator does.</summary>
    public void Dispose() => (inner as IDisposable)?.Dispose();

    private T? Run<T>(Func<T> call)
    {
        if (!availability.IsAvailable)
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
        if (!availability.IsAvailable)
        {
            return null;
        }

        try
        {
            var result = await call();
            availability.RecordSuccess();
            return result;
        }
        catch (Exception exception) when (!IsCancellationOf(exception, cancellationToken))
        {
            availability.RecordFailure(exception);
            return null;
        }
    }

    private async Task RunAsync(Func<Task> call, CancellationToken cancellationToken)
    {
        if (!availability.IsAvailable)
        {
            return;
        }

        try
        {
            await call();
            availability.RecordSuccess();
        }
        catch (Exception exception) when (!IsCancellationOf(exception, cancellationToken))
        {
            availability.RecordFailure(exception);
        }
    }

    private static bool IsCancellationOf(Exception exception, CancellationToken cancellationToken)
        => exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
}
