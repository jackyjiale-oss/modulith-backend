using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace TemplateName.UnitTests.Platform;

/// <summary>
/// The inner cache of <c>ResilientDistributedCache</c> in the tests: it counts every call and, while <see cref="Fails"/> is set, fails
/// each one the way an unreachable Redis does (a <see cref="RedisConnectionException"/>; a faulted task for the asynchronous methods).
/// While it works, every read finds <see cref="StoredValue"/>.
/// </summary>
internal sealed class ScriptedDistributedCache : IDistributedCache
{
    private int _calls;

    public static byte[] StoredValue { get; } = [1, 2, 3];

    public bool Fails { get; set; } = true;

    public int Calls => Volatile.Read(ref _calls);

    public byte[]? Get(string key) => Call(() => StoredValue);

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) => CallAsync<byte[]?>(StoredValue, cancellationToken);

    public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => Call(() => true);

    public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken cancellationToken = default)
        => CallAsync(true, cancellationToken);

    public void Refresh(string key) => Call(() => true);

    public Task RefreshAsync(string key, CancellationToken cancellationToken = default) => CallAsync(true, cancellationToken);

    public void Remove(string key) => Call(() => true);

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => CallAsync(true, cancellationToken);

    private T Call<T>(Func<T> result)
    {
        Interlocked.Increment(ref _calls);
        return Fails ? throw Failure() : result();
    }

    private Task<T> CallAsync<T>(T result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _calls);
        return Fails ? Task.FromException<T>(Failure()) : Task.FromResult(result);
    }

    private static RedisConnectionException Failure()
        => new(ConnectionFailureType.UnableToConnect, CommandFlags.None, "No connection is active/available to service this operation.", innerException: null, CommandStatus.Unknown);
}
