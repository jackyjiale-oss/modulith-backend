using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Infrastructure.Authorization;
using TemplateName.Platform.Redis;

namespace TemplateName.IntegrationTests.Redis;

/// <summary>Hosts on <c>Caching:Provider = redis</c> and what the Redis tests ask of them.</summary>
internal static class RedisTestHosts
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Another host of the factory (same SQL database, same clock) with <c>Caching:Provider = redis</c> on <paramref name="valkey"/>.
    /// Each call is a separate instance with its own local cache and its own Redis connection, as two API instances would be.
    /// <paramref name="settings"/> are further host settings.
    /// </summary>
    internal static WebApplicationFactory<Program> Create(
        IntegrationTestWebAppFactory factory,
        ValkeyFixture valkey,
        params (string Key, string Value)[] settings)
        => factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Caching:Provider", "redis").UseSetting("ConnectionStrings:Redis", valkey.ConnectionString);
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    /// <summary>Waits, in real time, until the host's Redis connection has noticed that Valkey is gone.</summary>
    internal static Task WaitUntilDisconnectedAsync(WebApplicationFactory<Program> host, CancellationToken cancellationToken)
    {
        var multiplexer = host.Services.GetRequiredService<IConnectionMultiplexer>();
        return EventuallyAsync(() => Task.FromResult(!multiplexer.IsConnected), "the host's Redis connection to drop", cancellationToken);
    }

    /// <summary>The Redis key under which <paramref name="host"/> keeps the permission set of <paramref name="userId"/>.</summary>
    internal static RedisKey PermissionKey(WebApplicationFactory<Program> host, Guid userId)
        => host.Services.GetRequiredService<IOptions<RedisCachingOptions>>().Value.InstanceName + PermissionCache.Key(userId);

    /// <summary>Whether Valkey holds the permission set of <paramref name="userId"/>, read through the host's own connection.</summary>
    internal static Task<bool> HoldsPermissionsOfAsync(WebApplicationFactory<Program> host, Guid userId)
        => host.Services.GetRequiredService<IConnectionMultiplexer>().GetDatabase().KeyExistsAsync(PermissionKey(host, userId));

    /// <summary>Waits, in real time, until the host's Redis connection is up again after a restart and answers a <c>PING</c>.</summary>
    internal static Task WaitUntilConnectedAsync(WebApplicationFactory<Program> host, CancellationToken cancellationToken)
    {
        var multiplexer = host.Services.GetRequiredService<IConnectionMultiplexer>();
        return EventuallyAsync(
            async () =>
            {
                if (!multiplexer.IsConnected)
                {
                    return false;
                }

                try
                {
                    await multiplexer.GetDatabase().PingAsync();
                    return true;
                }
                catch (RedisException)
                {
                    return false;
                }
            },
            "the host's Redis connection to come back",
            cancellationToken);
    }

    /// <summary>Polls <paramref name="condition"/> in real time until it holds, for at most 30 seconds.</summary>
    internal static async Task EventuallyAsync(Func<Task<bool>> condition, string description, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        while (!await condition())
        {
            if (waited.Elapsed > WaitLimit)
            {
                throw new TimeoutException($"Waited {WaitLimit} for {description}.");
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }
}
