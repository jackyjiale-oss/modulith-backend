using System.Threading.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;
using TemplateName.Infrastructure.Common;
using TemplateName.Platform.Redis;
using TemplateName.UnitTests.Application;
using TemplateName.Web.Common.Security;

namespace TemplateName.UnitTests.Platform;

/// <summary>
/// The Redis fixed-window limiter against a scripted <see cref="IDatabase"/>: what it sends, how it reads the answer, and how it falls back
/// to an in-memory window of the same limit when Redis fails or the shared circuit is open (spec section 4, D5).
/// </summary>
public sealed class RedisFixedWindowLimiterTests
{
    private const string Policy = "global";
    private const string PartitionKey = "ip:203.0.113.1";

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMilliseconds(500);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly ScriptedRedisDatabase _redis = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task One_script_runs_on_the_prefixed_policy_and_partition_key_with_the_window_in_milliseconds()
    {
        var (factory, _) = CreateFactory();
        using var limiter = factory.Create(Policy, "ip:::1", 3, Window);

        using var lease = await limiter.AcquireAsync(cancellationToken: Ct);

        lease.IsAcquired.ShouldBeTrue();
        _redis.Calls.ShouldBe(1);
        _redis.LastKeys.ShouldBe([(RedisKey)"TemplateName:rl:global:ip:::1"]);
        _redis.LastValues.ShouldBe([(RedisValue)1, (RedisValue)60_000]);

        // The key is never part of the script: the script text is the same for every key and is cached by its hash.
        _redis.LastScript.ShouldNotBeNull().ShouldNotContain("ip:::1");
        _redis.LastScript.ShouldContain("PEXPIRE");
    }

    [Fact]
    public async Task A_count_within_the_limit_is_acquired_and_above_it_refused_with_retry_after_from_the_ttl()
    {
        var (factory, _) = CreateFactory();
        using var limiter = factory.Create(Policy, PartitionKey, 3, Window);

        _redis.Count = 3;
        using var last = await limiter.AcquireAsync(cancellationToken: Ct);
        _redis.Count = 4;
        _redis.TimeToLiveMilliseconds = 41_500;
        using var refused = await limiter.AcquireAsync(cancellationToken: Ct);

        last.IsAcquired.ShouldBeTrue();
        last.TryGetMetadata(MetadataName.RetryAfter, out _).ShouldBeFalse();
        refused.IsAcquired.ShouldBeFalse();
        refused.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).ShouldBeTrue();
        retryAfter.ShouldBe(TimeSpan.FromMilliseconds(41_500));
        refused.MetadataNames.ShouldBe([MetadataName.RetryAfter.Name]);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Retry_after_is_at_least_one_second(long timeToLiveMilliseconds)
    {
        var (factory, _) = CreateFactory();
        using var limiter = factory.Create(Policy, PartitionKey, 3, Window);
        _redis.Count = 9;
        _redis.TimeToLiveMilliseconds = timeToLiveMilliseconds;

        using var refused = await limiter.AcquireAsync(cancellationToken: Ct);

        refused.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).ShouldBeTrue();
        retryAfter.ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_Redis_failure_counts_the_request_in_memory_with_the_same_limit()
    {
        // A threshold above the number of calls keeps the circuit closed, so every acquire really tries Redis first.
        var (factory, availability) = CreateFactory(new RedisCachingOptions { FailureThreshold = 100 });
        using var limiter = factory.Create(Policy, PartitionKey, 2, Window);
        _redis.Fails = true;

        using var first = await limiter.AcquireAsync(cancellationToken: Ct);
        using var second = await limiter.AcquireAsync(cancellationToken: Ct);
        using var refused = await limiter.AcquireAsync(cancellationToken: Ct);

        first.IsAcquired.ShouldBeTrue();
        second.IsAcquired.ShouldBeTrue();
        refused.IsAcquired.ShouldBeFalse();
        refused.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter).ShouldBeTrue();
        retryAfter.ShouldBe(Window);
        _redis.Calls.ShouldBe(3);
        availability.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task While_the_circuit_is_open_Redis_is_not_called_and_the_same_in_memory_window_keeps_counting()
    {
        var (factory, availability) = CreateFactory();
        using var limiter = factory.Create(Policy, PartitionKey, 5, Window);
        _redis.Fails = true;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var counted = await limiter.AcquireAsync(cancellationToken: Ct);
            counted.IsAcquired.ShouldBeTrue();
        }

        availability.IsAvailable.ShouldBeFalse();
        _redis.Fails = false;

        // Three requests are in the in-memory window already; two more fit, through either acquire, and the next is refused.
        using var fourth = await limiter.AcquireAsync(cancellationToken: Ct);
        using var fifth = limiter.AttemptAcquire();
        using var refused = limiter.AttemptAcquire();
        using var refusedAsync = await limiter.AcquireAsync(cancellationToken: Ct);

        fourth.IsAcquired.ShouldBeTrue();
        fifth.IsAcquired.ShouldBeTrue();
        refused.IsAcquired.ShouldBeFalse();
        refused.TryGetMetadata(MetadataName.RetryAfter, out _).ShouldBeTrue();
        refusedAsync.IsAcquired.ShouldBeFalse();
        _redis.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task After_the_break_one_probe_goes_to_Redis_and_its_success_brings_counting_back_to_Redis()
    {
        var (factory, availability) = CreateFactory();
        using var limiter = factory.Create(Policy, PartitionKey, 5, Window);
        await FailThreeTimesAsync(limiter);
        _time.Advance(BreakDuration);
        _redis.Fails = false;
        _redis.Hang = true;

        // The probe waits on Redis; a concurrent acquire meanwhile is counted in memory without calling it.
        var probe = limiter.AcquireAsync(cancellationToken: Ct).AsTask();
        using (var concurrent = await limiter.AcquireAsync(cancellationToken: Ct))
        {
            concurrent.IsAcquired.ShouldBeTrue();
        }

        _redis.Calls.ShouldBe(4);
        _redis.Hang = false;

        // The probe gives up after the command timeout: a failure, counted in memory, and another break.
        _time.Advance(CommandTimeout);
        using (var givenUp = await probe)
        {
            givenUp.IsAcquired.ShouldBeTrue();
        }

        availability.IsAvailable.ShouldBeFalse();

        _time.Advance(BreakDuration);
        _redis.Count = 1;
        using var throughRedis = await limiter.AcquireAsync(cancellationToken: Ct);
        using var again = await limiter.AcquireAsync(cancellationToken: Ct);

        throughRedis.IsAcquired.ShouldBeTrue();
        again.IsAcquired.ShouldBeTrue();
        availability.IsAvailable.ShouldBeTrue();
        _redis.Calls.ShouldBe(6);
    }

    [Fact]
    public async Task A_cancelled_caller_is_not_counted_and_does_not_keep_the_probe()
    {
        var (factory, availability) = CreateFactory(new RedisCachingOptions { FailureThreshold = 1 });
        using var other = factory.Create(Policy, "ip:203.0.113.2", 1, Window);
        using var limiter = factory.Create(Policy, PartitionKey, 1, Window);

        // Another partition's failure opens the shared circuit; this limiter's in-memory window is still empty.
        _redis.Fails = true;
        using (var failed = await other.AcquireAsync(cancellationToken: Ct))
        {
            failed.IsAcquired.ShouldBeTrue();
        }

        _time.Advance(BreakDuration);
        _redis.Fails = false;
        _redis.Hang = true;
        using var cancellation = new CancellationTokenSource();

        var probe = limiter.AcquireAsync(cancellationToken: cancellation.Token).AsTask();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => probe);
        availability.IsAvailable.ShouldBeTrue();
        limiter.GetStatistics().ShouldNotBeNull().TotalSuccessfulLeases.ShouldBe(0);
        limiter.GetStatistics().ShouldNotBeNull().TotalFailedLeases.ShouldBe(0);

        // The probe slot is free: the next acquire calls Redis. Redis fails, so it is counted in memory, where the one permit is unused.
        _redis.Hang = false;
        _redis.Fails = true;
        using var next = await limiter.AcquireAsync(cancellationToken: Ct);

        next.IsAcquired.ShouldBeTrue();
        _redis.Calls.ShouldBe(3);
    }

    [Fact]
    public async Task A_caller_cancelled_before_the_call_gets_its_cancellation_and_nothing_is_called()
    {
        var (factory, availability) = CreateFactory();
        using var limiter = factory.Create(Policy, PartitionKey, 1, Window);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Should.ThrowAsync<OperationCanceledException>(async () => await limiter.AcquireAsync(cancellationToken: cancellation.Token));
        }

        _redis.Calls.ShouldBe(0);
        availability.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public async Task A_call_that_hangs_is_given_up_after_the_command_timeout_and_counted_in_memory()
    {
        var (factory, availability) = CreateFactory(new RedisCachingOptions { FailureThreshold = 1 });
        using var limiter = factory.Create(Policy, PartitionKey, 1, Window);
        _redis.Hang = true;

        var acquire = limiter.AcquireAsync(cancellationToken: Ct).AsTask();
        _time.Advance(CommandTimeout - TimeSpan.FromMilliseconds(1));
        acquire.IsCompleted.ShouldBeFalse();

        _time.Advance(TimeSpan.FromMilliseconds(1));
        using var lease = await acquire;

        lease.IsAcquired.ShouldBeTrue();
        availability.IsAvailable.ShouldBeFalse();
        using var refused = await limiter.AcquireAsync(cancellationToken: Ct);
        refused.IsAcquired.ShouldBeFalse();
        _redis.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task AttemptAcquire_while_Redis_can_be_called_refuses_without_counting_so_the_caller_awaits_AcquireAsync()
    {
        // The ASP.NET Core middleware tries AttemptAcquire first and calls AcquireAsync only when it is refused; a synchronous answer
        // from memory would keep every request away from Redis.
        var (factory, _) = CreateFactory();
        using var limiter = factory.Create(Policy, PartitionKey, 1, Window);

        using var attempt = limiter.AttemptAcquire();

        attempt.IsAcquired.ShouldBeFalse();
        attempt.TryGetMetadata(MetadataName.RetryAfter, out _).ShouldBeFalse();
        _redis.Calls.ShouldBe(0);
        limiter.GetStatistics().ShouldNotBeNull().TotalFailedLeases.ShouldBe(0);

        using var acquired = await limiter.AcquireAsync(cancellationToken: Ct);
        acquired.IsAcquired.ShouldBeTrue();
        _redis.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_permit_count_above_the_limit_is_rejected_like_the_built_in_limiter()
    {
        var (factory, _) = CreateFactory();
        using var limiter = factory.Create(Policy, PartitionKey, 2, Window);

        Should.Throw<ArgumentOutOfRangeException>(() => limiter.AttemptAcquire(3));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await limiter.AcquireAsync(3, Ct));
        _redis.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task The_limiter_is_idle_since_its_last_acquire_unless_its_in_memory_window_holds_permits()
    {
        var (factory, _) = CreateFactory(new RedisCachingOptions { FailureThreshold = 100 });
        using var limiter = factory.Create(Policy, PartitionKey, 2, Window);
        using (await limiter.AcquireAsync(cancellationToken: Ct))
        {
        }

        _time.Advance(TimeSpan.FromSeconds(12));
        limiter.IdleDuration.ShouldBe(TimeSpan.FromSeconds(12));

        // Counted in memory: the partition must not be dropped (and its count lost) before that window is over.
        _redis.Fails = true;
        using (await limiter.AcquireAsync(cancellationToken: Ct))
        {
        }

        _time.Advance(TimeSpan.FromSeconds(12));
        limiter.IdleDuration.ShouldBeNull();
    }

    [Fact]
    public async Task Disposing_a_limiter_leaves_the_shared_connection_open_and_refuses_further_acquires()
    {
        var (factory, _) = CreateFactory();
        var limiter = factory.Create(Policy, PartitionKey, 2, Window);
        using (await limiter.AcquireAsync(cancellationToken: Ct))
        {
        }

        limiter.Dispose();
        await limiter.DisposeAsync();

        _redis.Multiplexer.DidNotReceive().Dispose();
        await _redis.Multiplexer.DidNotReceive().DisposeAsync();
        _redis.Multiplexer.DidNotReceive().Close(Arg.Any<bool>());
        await _redis.Multiplexer.DidNotReceive().CloseAsync(Arg.Any<bool>());
        Should.Throw<ObjectDisposedException>(() => limiter.AttemptAcquire());
        await Should.ThrowAsync<ObjectDisposedException>(async () => await limiter.AcquireAsync(cancellationToken: Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void With_provider_redis_the_Redis_factory_replaces_the_in_memory_one_in_either_order(bool httpSecurityFirst)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:Provider"] = "redis",
            ["ConnectionStrings:Redis"] = "localhost:6379",
            ["ConnectionStrings:Database"] = "Server=.;Database=Tests",
        }).Build();
        var services = new ServiceCollection().AddLogging().AddInfrastructureCommon(configuration);
        services.AddSingleton(_redis.Multiplexer);
        if (httpSecurityFirst)
        {
            services.AddHttpSecurity(configuration).AddPlatformRedis(configuration);
        }
        else
        {
            services.AddPlatformRedis(configuration).AddHttpSecurity(configuration);
        }

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IFixedWindowLimiterFactory>().ShouldBeOfType<RedisFixedWindowLimiterFactory>();
        services.Count(descriptor => descriptor.ServiceType == typeof(IFixedWindowLimiterFactory)).ShouldBe(1);
    }

    [Fact]
    public void With_provider_memory_the_in_memory_factory_stays()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Database"] = "Server=.;Database=Tests",
        }).Build();
        var services = new ServiceCollection().AddLogging().AddInfrastructureCommon(configuration).AddHttpSecurity(configuration).AddPlatformRedis(configuration);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IFixedWindowLimiterFactory>().ShouldBeOfType<InMemoryFixedWindowLimiterFactory>();
    }

    private (RedisFixedWindowLimiterFactory Factory, RedisAvailability Availability) CreateFactory(RedisCachingOptions? options = null)
    {
        var settings = Options.Create(options ?? new RedisCachingOptions());
        var availability = new RedisAvailability(_time, settings, new RecordingLogger<RedisAvailability>());
        return (new RedisFixedWindowLimiterFactory(_redis.Multiplexer, availability, _time, settings), availability);
    }

    private async Task FailThreeTimesAsync(RateLimiter limiter)
    {
        _redis.Fails = true;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var lease = await limiter.AcquireAsync(cancellationToken: Ct);
        }

        _redis.Calls.ShouldBe(3);
    }
}
