using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TemplateName.Infrastructure.Common.Caching;
using TemplateName.Web.Common.Security;

namespace TemplateName.Platform.Redis;

public static class PlatformRedisServiceCollectionExtensions
{
    /// <summary>The name of the readiness check, tagged <c>ready</c>.</summary>
    internal const string HealthCheckName = "redis";

    /// <summary>The StackExchange.Redis default heartbeat; a shorter <c>CommandTimeout</c> shortens it.</summary>
    private static readonly TimeSpan MaximumHeartbeatInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Makes Redis the shared second level of <c>HybridCache</c> when <c>Caching:Provider</c> is <c>redis</c>; does nothing otherwise
    /// (ADR 0027). It registers the <c>Caching:Redis</c> options and <c>ConnectionStrings:Redis</c> (both validated on start), one
    /// <see cref="IConnectionMultiplexer"/> for the whole instance (<c>abortConnect=false</c>, so a Redis that is down at start does not
    /// stop the host, and connect, synchronous and asynchronous timeouts of <c>Caching:Redis:CommandTimeout</c>), the
    /// <see cref="RedisAvailability"/> circuit, <c>AddStackExchangeRedisCache</c> on that connection with keys prefixed by
    /// <c>Caching:Redis:InstanceName</c> and wrapped by a decorator that never lets a Redis failure reach the caller,
    /// <see cref="RedisFixedWindowLimiterFactory"/> in place of the in-memory <see cref="IFixedWindowLimiterFactory"/> (so the
    /// <c>global</c>, <c>auth-strict</c> and <c>notifications-hub-tickets</c> windows are shared by every instance, and counted in memory
    /// while Redis is down), and the readiness check <c>redis</c> (a <c>PING</c>; failure status <see cref="HealthStatus.Degraded"/>, tag
    /// <c>ready</c>). Call it after <c>AddInfrastructureCommon</c> (before or after <c>AddHttpSecurity</c>). Only the host references this
    /// project, so a solution without Redis deletes the project and this call.
    /// </summary>
    public static IServiceCollection AddPlatformRedis(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Read now, because it decides what is registered; an unknown value fails here, naming Caching:Provider.
        var caching = configuration.GetSection(CachingOptions.SectionName).Get<CachingOptions>() ?? new CachingOptions();
        if (caching.Provider != CachingProvider.Redis)
        {
            return services;
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<RedisCachingOptions>()
            .Bind(configuration.GetSection(RedisCachingOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                redis => redis.CommandTimeout < redis.BreakDuration,
                "Caching:Redis:CommandTimeout must be shorter than Caching:Redis:BreakDuration.")
            .ValidateOnStart();
        services.AddOptions<RedisConnectionStringOptions>()
            .Bind(configuration.GetSection(RedisConnectionStringOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                connection => string.IsNullOrEmpty(connection.Redis) || IsValidConnectionString(connection.Redis),
                "ConnectionStrings:Redis is not a valid Redis configuration string (for example localhost:6379).")
            .ValidateOnStart();

        services.TryAddSingleton<RedisAvailability>();
        services.TryAddSingleton<IConnectionMultiplexer>(provider => ConnectionMultiplexer.Connect(CreateConfiguration(
            provider.GetRequiredService<IOptions<RedisConnectionStringOptions>>().Value.Redis,
            provider.GetRequiredService<IOptions<RedisCachingOptions>>().Value)));

        // HybridCache picks up the registered IDistributedCache as its second level by itself.
        services.AddStackExchangeRedisCache(_ => { });
        services.AddOptions<RedisCacheOptions>().Configure<IOptions<RedisCachingOptions>, IServiceProvider>((cache, redis, provider) =>
        {
            cache.InstanceName = redis.Value.InstanceName;
            cache.ConnectionMultiplexerFactory = () => Task.FromResult(provider.GetRequiredService<IConnectionMultiplexer>());
        });
        DecorateDistributedCache(services);

        // Replace, not TryAdd: AddHttpSecurity may already have registered the in-memory default (it only adds one when none exists).
        services.Replace(ServiceDescriptor.Singleton<IFixedWindowLimiterFactory, RedisFixedWindowLimiterFactory>());

        services.AddHealthChecks().AddCheck<RedisHealthCheck>(HealthCheckName, HealthStatus.Degraded, ["ready"]);

        return services;
    }

    /// <summary>
    /// The connection settings: <paramref name="connectionString"/> with, whatever the string says, <c>abortConnect=false</c>, the
    /// connect, sync and async timeouts set to <see cref="RedisCachingOptions.CommandTimeout"/>, a heartbeat no longer than that
    /// timeout (StackExchange.Redis checks for timed-out commands on each heartbeat, by default once a second), and the backlog policy
    /// <see cref="BacklogPolicy.FailFast"/>: while the connection is down a command fails at once instead of waiting in a queue for the
    /// reconnect. The callers bound every call by <see cref="RedisCachingOptions.CommandTimeout"/> on top of this.
    /// </summary>
    internal static ConfigurationOptions CreateConfiguration(string connectionString, RedisCachingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configuration = ConfigurationOptions.Parse(connectionString);
        var timeout = (int)options.CommandTimeout.TotalMilliseconds;
        configuration.AbortOnConnectFail = false;
        configuration.ConnectTimeout = timeout;
        configuration.SyncTimeout = timeout;
        configuration.AsyncTimeout = timeout;
        configuration.BacklogPolicy = BacklogPolicy.FailFast;
        configuration.HeartbeatInterval = TimeSpan.FromMilliseconds(Math.Min(timeout, MaximumHeartbeatInterval.TotalMilliseconds));
        return configuration;
    }

    private static bool IsValidConnectionString(string connectionString)
    {
        try
        {
            return ConfigurationOptions.Parse(connectionString).EndPoints.Count > 0;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces the <see cref="IDistributedCache"/> that <c>AddStackExchangeRedisCache</c> registered with
    /// <see cref="ResilientDistributedCache"/> around it: a factory builds the original from its own registration and wraps it. The
    /// container does not track an instance a factory builds inside itself, so the inner <c>RedisCache</c> is never disposed, and its
    /// <c>Dispose</c>, which would close the shared connection, never runs.
    /// </summary>
    private static void DecorateDistributedCache(IServiceCollection services)
    {
        var redis = services.Last(descriptor => descriptor.ServiceType == typeof(IDistributedCache) && !descriptor.IsKeyedService);

        services.Replace(ServiceDescriptor.Singleton<IDistributedCache>(provider => new ResilientDistributedCache(
            (IDistributedCache)CreateInstance(provider, redis),
            provider.GetRequiredService<RedisAvailability>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<IOptions<RedisCachingOptions>>())));
    }

    private static object CreateInstance(IServiceProvider provider, ServiceDescriptor descriptor)
        => descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(provider)
            ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
}
