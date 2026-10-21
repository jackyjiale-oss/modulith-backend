using System.Net;
using System.Net.Sockets;
using Testcontainers.Redis;

namespace TemplateName.IntegrationTests.Infrastructure;

/// <summary>
/// Runs Valkey (BSD-3, Redis protocol; ADR 0027) in a container for the Redis tests. The image tag is pinned and matches the one in
/// <c>docker-compose.yml</c>; a test keeps the two in step. The container is published on a <b>fixed</b> host port, chosen free when the
/// fixture is created, so a test that stops and starts it (<see cref="StopAsync"/>, <see cref="StartAsync"/>) gets it back at the same
/// address: the hosts it built keep their connection string and their multiplexer reconnects by itself, as it would to a restarted
/// server in production. (Docker would map a new random port on restart otherwise.)
/// </summary>
public sealed class ValkeyFixture : IAsyncLifetime
{
    /// <summary>The pinned image. Change it together with <c>docker-compose.yml</c>.</summary>
    public const string Image = "valkey/valkey:9.1.2-alpine";

    private const int ValkeyPort = 6379;

    private readonly int _hostPort = FreeLoopbackPort();
    private readonly RedisContainer _container;
    private bool _isStopped;

    public ValkeyFixture()
    {
        _container = new RedisBuilder(Image).WithPortBinding(_hostPort, ValkeyPort).Build();
    }

    /// <summary>The <c>ConnectionStrings:Redis</c> value of a host that uses this Valkey; the same before and after a restart.</summary>
    public string ConnectionString => $"{_container.Hostname}:{_hostPort}";

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync(TestContext.Current.CancellationToken);

        var mappedPort = _container.GetMappedPublicPort(ValkeyPort);
        if (mappedPort != _hostPort)
        {
            throw new InvalidOperationException($"Valkey was published on port {mappedPort}, not on the fixed port {_hostPort}.");
        }
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Stops the container (connections are refused). A test that calls it calls <see cref="StartAsync"/> in a <c>finally</c>.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _container.StopAsync(cancellationToken);
        _isStopped = true;
    }

    /// <summary>Starts a stopped container again on the same host port and waits until it answers; does nothing when it is running.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_isStopped)
        {
            return;
        }

        await _container.StartAsync(cancellationToken);
        _isStopped = false;
    }

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
