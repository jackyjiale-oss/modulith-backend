using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TemplateName.Platform.Redis;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Platform;

/// <summary>
/// The decorator around the Redis L2 of <c>HybridCache</c>: a failing Redis never reaches the caller, and after
/// <c>Caching:Redis:FailureThreshold</c> (3) consecutive failures Redis is not called at all for <c>BreakDuration</c> (30 s).
/// </summary>
public sealed class ResilientDistributedCacheTests
{
    private const string Key = "auth:perm:3f2b8a52-6c1e-4c39-9d1e-2a4f5b6c7d8e";

    private static readonly byte[] Value = [9, 9, 9];
    private static readonly DistributedCacheEntryOptions EntryOptions = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) };
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly ScriptedDistributedCache _inner = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Read_write_refresh_and_remove_never_throw_when_the_inner_cache_fails()
    {
        // A threshold above the number of calls keeps the circuit closed, so every call reaches the failing cache.
        var (cache, _) = CreateCache(new RedisCachingOptions { FailureThreshold = 100 });

        cache.Get(Key).ShouldBeNull();
        (await cache.GetAsync(Key, Ct)).ShouldBeNull();
        Should.NotThrow(() => cache.Set(Key, Value, EntryOptions));
        await Should.NotThrowAsync(() => cache.SetAsync(Key, Value, EntryOptions, Ct));
        Should.NotThrow(() => cache.Refresh(Key));
        await Should.NotThrowAsync(() => cache.RefreshAsync(Key, Ct));
        Should.NotThrow(() => cache.Remove(Key));
        await Should.NotThrowAsync(() => cache.RemoveAsync(Key, Ct));

        _inner.Calls.ShouldBe(8);
    }

    [Fact]
    public async Task After_three_consecutive_failures_the_inner_cache_is_not_called_for_thirty_seconds()
    {
        var (cache, availability) = CreateCache();
        await FailThreeTimesAsync(cache);

        // Redis is back, but the circuit stays open for the whole break: every call is answered as a miss without reaching it.
        _inner.Fails = false;
        _time.Advance(BreakDuration - TimeSpan.FromMilliseconds(1));

        availability.IsAvailable.ShouldBeFalse();
        cache.Get(Key).ShouldBeNull();
        (await cache.GetAsync(Key, Ct)).ShouldBeNull();
        cache.Set(Key, Value, EntryOptions);
        await cache.SetAsync(Key, Value, EntryOptions, Ct);
        cache.Refresh(Key);
        await cache.RefreshAsync(Key, Ct);
        cache.Remove(Key);
        await cache.RemoveAsync(Key, Ct);
        _inner.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task After_three_consecutive_failures_the_inner_cache_is_called_again_after_the_break()
    {
        var (cache, availability) = CreateCache();
        await FailThreeTimesAsync(cache);
        _inner.Fails = false;

        _time.Advance(BreakDuration);

        availability.IsAvailable.ShouldBeTrue();
        (await cache.GetAsync(Key, Ct)).ShouldBe(ScriptedDistributedCache.StoredValue);
        cache.Get(Key).ShouldBe(ScriptedDistributedCache.StoredValue);
        _inner.Calls.ShouldBe(5);
    }

    [Fact]
    public async Task A_failure_after_the_break_opens_the_circuit_again_at_once()
    {
        var (cache, availability) = CreateCache();
        await FailThreeTimesAsync(cache);
        _time.Advance(BreakDuration);

        // The first call after the break still fails: one failure is enough to open the circuit for another break.
        (await cache.GetAsync(Key, Ct)).ShouldBeNull();
        _inner.Calls.ShouldBe(4);
        availability.IsAvailable.ShouldBeFalse();

        _time.Advance(BreakDuration - TimeSpan.FromMilliseconds(1));
        (await cache.GetAsync(Key, Ct)).ShouldBeNull();
        _inner.Calls.ShouldBe(4);
    }

    [Fact]
    public async Task A_success_resets_the_failure_count()
    {
        var (cache, availability) = CreateCache();
        (await cache.GetAsync(Key, Ct)).ShouldBeNull();
        await cache.SetAsync(Key, Value, EntryOptions, Ct);

        _inner.Fails = false;
        (await cache.GetAsync(Key, Ct)).ShouldBe(ScriptedDistributedCache.StoredValue);

        // Two more failures make two in a row, not four: the circuit stays closed and the next call still reaches Redis.
        _inner.Fails = true;
        await cache.RemoveAsync(Key, Ct);
        await cache.RefreshAsync(Key, Ct);
        availability.IsAvailable.ShouldBeTrue();

        _inner.Fails = false;
        (await cache.GetAsync(Key, Ct)).ShouldBe(ScriptedDistributedCache.StoredValue);
        _inner.Calls.ShouldBe(6);
    }

    [Fact]
    public async Task A_cancelled_call_is_not_a_failure_and_its_cancellation_reaches_the_caller()
    {
        var (cache, availability) = CreateCache();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Should.ThrowAsync<OperationCanceledException>(() => cache.GetAsync(Key, cancellation.Token));
        }

        availability.IsAvailable.ShouldBeTrue();
    }

    private (ResilientDistributedCache Cache, RedisAvailability Availability) CreateCache(RedisCachingOptions? options = null)
    {
        var availability = new RedisAvailability(_time, Options.Create(options ?? new RedisCachingOptions()), new RecordingLogger<RedisAvailability>());
        return (new ResilientDistributedCache(_inner, availability), availability);
    }

    private async Task FailThreeTimesAsync(ResilientDistributedCache cache)
    {
        (await cache.GetAsync(Key, Ct)).ShouldBeNull();
        await cache.SetAsync(Key, Value, EntryOptions, Ct);
        cache.Remove(Key);
        _inner.Calls.ShouldBe(3);
    }
}
