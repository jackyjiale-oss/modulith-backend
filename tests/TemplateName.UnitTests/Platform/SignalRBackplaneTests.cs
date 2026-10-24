using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using StackExchange.Redis;
using TemplateName.Infrastructure.Common;
using TemplateName.Platform.Redis;

namespace TemplateName.UnitTests.Platform;

/// <summary>
/// The SignalR backplane that <c>AddPlatformRedis</c> registers with <c>Caching:Provider = redis</c>: SignalR's Redis hub lifetime manager
/// on the instance's one shared connection, its channels under <c>{InstanceName}signalr</c>, and a connection the backplane can never close.
/// </summary>
public sealed class SignalRBackplaneTests
{
    [Fact]
    public void Redis_provider_uses_the_Redis_hub_lifetime_manager_on_the_shared_connection()
    {
        var connection = SharedConnection();
        using var services = BuildServices("redis", connection);

        services.GetRequiredService<HubLifetimeManager<PingHub>>().ShouldBeOfType<RedisHubLifetimeManager<PingHub>>();

        // The lifetime manager connects as it is created: through the shared connection, not one of its own.
        connection.Received().GetSubscriber(Arg.Any<object?>());
    }

    [Fact]
    public void Memory_provider_keeps_the_in_memory_hub_lifetime_manager()
    {
        using var services = BuildServices("memory", connection: null);

        services.GetRequiredService<HubLifetimeManager<PingHub>>().ShouldBeOfType<DefaultHubLifetimeManager<PingHub>>();
    }

    [Fact]
    public void Channels_are_prefixed_with_the_instance_name_and_signalr()
    {
        var configuration = PlatformRedisServiceCollectionExtensions.CreateConfiguration(
            "localhost:6379",
            new RedisCachingOptions { InstanceName = "Orders:" });

        configuration.ChannelPrefix.ShouldBe(RedisChannel.Literal("Orders:signalr"));
    }

    [Fact]
    public async Task Disposing_the_backplane_does_not_close_the_shared_connection()
    {
        // RedisHubLifetimeManager.Dispose disposes the connection it got from its factory, which here is the instance's shared one.
        var connection = SharedConnection();
        var services = BuildServices("redis", connection);
        services.GetRequiredService<HubLifetimeManager<PingHub>>();
        connection.Received().GetSubscriber(Arg.Any<object?>());

        // What the host does at shutdown: the container disposes the lifetime manager (it owns the connection it created itself; this
        // one it was handed, so only the lifetime manager could close it).
        await services.DisposeAsync();

        connection.DidNotReceive().Dispose();
        await connection.DidNotReceive().DisposeAsync();
        connection.DidNotReceive().Close(Arg.Any<bool>());
        await connection.DidNotReceive().CloseAsync(Arg.Any<bool>());
    }

    /// <summary>A substitute shared connection whose subscriber fails every subscription, as a Redis that is down does.</summary>
    private static IConnectionMultiplexer SharedConnection()
    {
        var subscriber = Substitute.For<ISubscriber>();
        subscriber.SubscribeAsync(Arg.Any<RedisChannel>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromException<ChannelMessageQueue>(
                new RedisConnectionException(ConnectionFailureType.UnableToConnect, CommandFlags.None, "Redis is down.")));
        var connection = Substitute.For<IConnectionMultiplexer>();
        connection.GetSubscriber(Arg.Any<object?>()).Returns(subscriber);
        return connection;
    }

    private static ServiceProvider BuildServices(string provider, IConnectionMultiplexer? connection)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Caching:Provider"] = provider,
            ["ConnectionStrings:Redis"] = "localhost:6379",
            ["ConnectionStrings:Database"] = "Server=.;Database=Tests",
        }).Build();
        var services = new ServiceCollection().AddLogging().AddInfrastructureCommon(configuration);
        if (connection is not null)
        {
            services.AddSingleton(connection);
        }

        // What a module with a hub does (Notifications' AddHub), in either order with AddPlatformRedis.
        services.AddSignalR();
        services.AddPlatformRedis(configuration);
        return services.BuildServiceProvider();
    }

    // Public: SignalR builds the closed generic lifetime manager for it.
    public sealed class PingHub : Hub;
}
