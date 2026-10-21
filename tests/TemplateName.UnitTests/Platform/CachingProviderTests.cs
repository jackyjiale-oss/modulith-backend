using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;
using TemplateName.Infrastructure.Common;
using TemplateName.Infrastructure.Common.Caching;
using TemplateName.Modules.Auth.Infrastructure.Authorization;
using TemplateName.Platform.Redis;

namespace TemplateName.UnitTests.Platform;

/// <summary>
/// <c>Caching:Provider</c> (<c>memory</c>, <c>redis</c> or <c>none</c>) as <c>AddInfrastructureCommon</c> and <c>AddPlatformRedis</c>
/// read it, and the permission cache key.
/// </summary>
public sealed class CachingProviderTests
{
    private const HybridCacheEntryFlags BothLayersDisabled = HybridCacheEntryFlags.DisableLocalCache | HybridCacheEntryFlags.DisableDistributedCache;

    [Fact]
    public void Provider_defaults_to_memory()
    {
        using var services = BuildServices([]);

        services.GetRequiredService<IOptions<CachingOptions>>().Value.Provider.ShouldBe(CachingProvider.Memory);
        (services.GetRequiredService<IOptions<HybridCacheOptions>>().Value.DefaultEntryOptions?.Flags ?? HybridCacheEntryFlags.None)
            .HasFlag(HybridCacheEntryFlags.DisableLocalCache).ShouldBeFalse();
        services.GetService<IConnectionMultiplexer>().ShouldBeNull();
        services.GetService<IDistributedCache>().ShouldBeNull();
        services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.ShouldNotContain(registration => registration.Name == "redis");
        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Theory]
    [InlineData("memory", CachingProvider.Memory)]
    [InlineData("redis", CachingProvider.Redis)]
    [InlineData("none", CachingProvider.None)]
    [InlineData("Redis", CachingProvider.Redis)]
    public void Provider_is_read_without_regard_to_case(string value, CachingProvider expected)
    {
        using var services = BuildServices(new() { ["Caching:Provider"] = value, ["ConnectionStrings:Redis"] = "localhost:6379" });

        services.GetRequiredService<IOptions<CachingOptions>>().Value.Provider.ShouldBe(expected);
    }

    [Fact]
    public void Provider_none_disables_both_cache_layers_by_default()
    {
        using var services = BuildServices(new() { ["Caching:Provider"] = "none" });

        var defaults = services.GetRequiredService<IOptions<HybridCacheOptions>>().Value.DefaultEntryOptions.ShouldNotBeNull();
        (defaults.Flags & BothLayersDisabled).ShouldBe(BothLayersDisabled);
        services.GetService<IConnectionMultiplexer>().ShouldBeNull();
    }

    [Fact]
    public void Redis_provider_without_a_connection_string_fails_on_start()
    {
        using var services = BuildServices(new() { ["Caching:Provider"] = "redis" });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("ConnectionStrings:Redis");
    }

    [Fact]
    public void Redis_provider_with_a_connection_string_registers_the_multiplexer_the_decorated_cache_and_the_readiness_check()
    {
        using var services = BuildServices(new() { ["Caching:Provider"] = "redis", ["ConnectionStrings:Redis"] = "localhost:6379" });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        services.GetRequiredService<IDistributedCache>().ShouldBeOfType<ResilientDistributedCache>();
        var check = services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Where(registration => registration.Name == "redis")
            .ShouldHaveSingleItem();
        check.FailureStatus.ShouldBe(HealthStatus.Degraded);
        check.Tags.ShouldBe(["ready"]);
    }

    [Fact]
    public void Redis_options_default_to_the_plan_values_and_the_timeouts_reach_the_connection()
    {
        using var services = BuildServices(new() { ["Caching:Provider"] = "redis", ["ConnectionStrings:Redis"] = "localhost:6379" });

        var options = services.GetRequiredService<IOptions<RedisCachingOptions>>().Value;
        options.InstanceName.ShouldBe("TemplateName:");
        options.FailureThreshold.ShouldBe(3);
        options.CommandTimeout.ShouldBe(TimeSpan.FromMilliseconds(500));
        options.BreakDuration.ShouldBe(TimeSpan.FromSeconds(30));

        var connection = PlatformRedisServiceCollectionExtensions.CreateConfiguration("localhost:6379,abortConnect=true,syncTimeout=5000", options);
        connection.AbortOnConnectFail.ShouldBeFalse();
        connection.ConnectTimeout.ShouldBe(500);
        connection.SyncTimeout.ShouldBe(500);
        connection.AsyncTimeout.ShouldBe(500);
        connection.HeartbeatInterval.ShouldBe(TimeSpan.FromMilliseconds(500));
        connection.BacklogPolicy.ShouldBeSameAs(BacklogPolicy.FailFast);
    }

    [Fact]
    public void The_heartbeat_is_never_longer_than_one_second_nor_than_the_command_timeout()
    {
        var slow = PlatformRedisServiceCollectionExtensions.CreateConfiguration(
            "localhost:6379",
            new RedisCachingOptions { CommandTimeout = TimeSpan.FromSeconds(5) });
        var fast = PlatformRedisServiceCollectionExtensions.CreateConfiguration(
            "localhost:6379",
            new RedisCachingOptions { CommandTimeout = TimeSpan.FromMilliseconds(100) });

        slow.HeartbeatInterval.ShouldBe(TimeSpan.FromSeconds(1));
        fast.HeartbeatInterval.ShouldBe(TimeSpan.FromMilliseconds(100));
    }

    [Theory]
    [InlineData("localhost:6379,notAnOption=1")]
    [InlineData(",abortConnect=false")]
    public void A_malformed_connection_string_fails_on_start(string connectionString)
    {
        using var services = BuildServices(new() { ["Caching:Provider"] = "redis", ["ConnectionStrings:Redis"] = connectionString });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("ConnectionStrings:Redis is not a valid Redis configuration string");
    }

    [Fact]
    public void A_command_timeout_not_shorter_than_the_break_fails_on_start()
    {
        // A call may outlive the break otherwise, and its late outcome would free the probe slot while a second probe runs.
        using var services = BuildServices(new()
        {
            ["Caching:Provider"] = "redis",
            ["ConnectionStrings:Redis"] = "localhost:6379",
            ["Caching:Redis:CommandTimeout"] = "00:00:05",
            ["Caching:Redis:BreakDuration"] = "00:00:05",
        });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("Caching:Redis:CommandTimeout must be shorter than Caching:Redis:BreakDuration");
    }

    [Fact]
    public async Task Disposing_the_cache_does_not_close_the_shared_connection()
    {
        // The readiness check (and later the limiter and the backplane) share the connection the cache got from its factory. RedisCache
        // releases a connection through IDatabase.Multiplexer, so the database must lead back to the shared connection.
        var connection = Substitute.For<IConnectionMultiplexer>();
        var database = Substitute.For<IDatabase>();
        database.Multiplexer.Returns(connection);
        connection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);
        var configuration = Configuration(new()
        {
            ["Caching:Provider"] = "redis",
            ["ConnectionStrings:Redis"] = "localhost:6379",
            ["ConnectionStrings:Database"] = "Server=.;Database=Tests",
        });
        var collection = new ServiceCollection().AddLogging().AddInfrastructureCommon(configuration);
        collection.AddSingleton(connection);
        collection.AddPlatformRedis(configuration);
        var services = collection.BuildServiceProvider();
        var cache = services.GetRequiredService<IDistributedCache>();
        await cache.GetAsync("auth:perm:any", TestContext.Current.CancellationToken);
        connection.Received().GetDatabase(Arg.Any<int>(), Arg.Any<object?>());

        // What the host does at shutdown: the container disposes the cache (and owns the connection it created itself; this one it
        // was handed, so only the cache could close it).
        (cache as IDisposable)?.Dispose();
        await services.DisposeAsync();

        connection.DidNotReceive().Dispose();
        await connection.DidNotReceive().DisposeAsync();
        connection.DidNotReceive().Close(Arg.Any<bool>());
        await connection.DidNotReceive().CloseAsync(Arg.Any<bool>());
    }

    [Theory]
    [InlineData("Caching:Redis:FailureThreshold", "0")]
    [InlineData("Caching:Redis:BreakDuration", "00:00:00")]
    [InlineData("Caching:Redis:CommandTimeout", "00:00:00")]
    [InlineData("Caching:Redis:InstanceName", "")]
    public void Invalid_redis_options_fail_on_start(string key, string value)
    {
        using var services = BuildServices(new() { ["Caching:Provider"] = "redis", ["ConnectionStrings:Redis"] = "localhost:6379", [key] = value });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.OptionsType.ShouldBe(typeof(RedisCachingOptions));
    }

    [Fact]
    public void Redis_provider_in_a_host_without_Platform_Redis_fails_on_start()
    {
        var configuration = Configuration(new() { ["Caching:Provider"] = "redis", ["ConnectionStrings:Database"] = "Server=.;Database=Tests" });
        using var services = new ServiceCollection().AddLogging().AddInfrastructureCommon(configuration).BuildServiceProvider();

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("AddPlatformRedis");
    }

    [Fact]
    public void Permission_key_is_auth_perm_userid()
    {
        var userId = Guid.Parse("3f2b8a52-6c1e-4c39-9d1e-2a4f5b6c7d8e");

        PermissionCache.Key(userId).ShouldBe("auth:perm:3f2b8a52-6c1e-4c39-9d1e-2a4f5b6c7d8e");
        PermissionCache.Lifetime.ShouldBe(TimeSpan.FromSeconds(30));
        PermissionCache.EntryOptions.LocalCacheExpiration.ShouldBe(TimeSpan.FromSeconds(30));
    }

    /// <summary>A host's caching services: the building block, an in-memory <see cref="HybridCache"/>, and the optional Redis project.</summary>
    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        settings.TryAdd("ConnectionStrings:Database", "Server=.;Database=Tests");
        var configuration = Configuration(settings);
        var services = new ServiceCollection().AddLogging().AddInfrastructureCommon(configuration);
        services.AddHybridCache();
        services.AddPlatformRedis(configuration);
        return services.BuildServiceProvider();
    }

    private static IConfiguration Configuration(Dictionary<string, string?> settings) => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
}
