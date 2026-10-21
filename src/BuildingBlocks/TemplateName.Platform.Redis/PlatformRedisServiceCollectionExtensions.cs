using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TemplateName.Infrastructure.Common.Caching;

namespace TemplateName.Platform.Redis;

public static class PlatformRedisServiceCollectionExtensions
{
    /// <summary>The name of the readiness check, tagged <c>ready</c>.</summary>
    internal const string HealthCheckName = "redis";

    /// <summary>
    /// Makes Redis the shared second level of <c>HybridCache</c> when <c>Caching:Provider</c> is <c>redis</c>; does nothing otherwise
    /// (ADR 0027). It registers the <c>Caching:Redis</c> options and <c>ConnectionStrings:Redis</c> (both validated on start), one
    /// <see cref="IConnectionMultiplexer"/> for the whole instance (<c>abortConnect=false</c>, so a Redis that is down at start does not
    /// stop the host, and connect, synchronous and asynchronous timeouts of <c>Caching:Redis:CommandTimeout</c>), the
    /// <see cref="RedisAvailability"/> circuit, <c>AddStackExchangeRedisCache</c> on that connection with keys prefixed by
    /// <c>Caching:Redis:InstanceName</c> and wrapped by a decorator that never lets a Redis failure reach the caller, and the readiness
    /// check <c>redis</c> (a <c>PING</c>; failure status <see cref="HealthStatus.Degraded"/>, tag <c>ready</c>). Call it after
    /// <c>AddInfrastructureCommon</c>. Only the host references this project, so a solution without Redis deletes the project and this
    /// call.
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
            .ValidateOnStart();
        services.AddOptions<RedisConnectionStringOptions>()
            .Bind(configuration.GetSection(RedisConnectionStringOptions.SectionName))
            .ValidateDataAnnotations()
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

        services.AddHealthChecks().AddCheck<RedisHealthCheck>(HealthCheckName, HealthStatus.Degraded, ["ready"]);

        return services;
    }

    /// <summary>
    /// The connection settings: <paramref name="connectionString"/> with <c>abortConnect=false</c> and every timeout set to
    /// <see cref="RedisCachingOptions.CommandTimeout"/>, whatever the string says, so a Redis that does not answer costs a request at
    /// most that long (and, once the circuit opens, nothing).
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
        return configuration;
    }

    /// <summary>
    /// Replaces the <see cref="IDistributedCache"/> that <c>AddStackExchangeRedisCache</c> registered with
    /// <see cref="ResilientDistributedCache"/> around it: a factory builds the original from its own registration and wraps it.
    /// </summary>
    private static void DecorateDistributedCache(IServiceCollection services)
    {
        var redis = services.Last(descriptor => descriptor.ServiceType == typeof(IDistributedCache) && !descriptor.IsKeyedService);

        services.Replace(ServiceDescriptor.Singleton<IDistributedCache>(provider => new ResilientDistributedCache(
            (IDistributedCache)CreateInstance(provider, redis),
            provider.GetRequiredService<RedisAvailability>())));
    }

    private static object CreateInstance(IServiceProvider provider, ServiceDescriptor descriptor)
        => descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(provider)
            ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
}
