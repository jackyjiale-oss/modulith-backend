using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application;
using TemplateName.Platform.Redis;

namespace TemplateName.IntegrationTests.Redis;

/// <summary>
/// Valkey stopped under a host on <c>Caching:Provider = redis</c> (spec section 4): the API keeps serving from the database, readiness is
/// <c>Degraded</c> (not <c>Unhealthy</c>, so a load balancer keeps the instance), and the cache returns once Redis does and the break
/// is over. Every test restarts Valkey before it ends; the fixture keeps its port, so the hosts reconnect by themselves.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisDegradedTests(IntegrationTestWebAppFactory factory, ValkeyFixture valkey) : IntegrationTestBase(factory)
{
    private const string RolesRoute = "/api/v1/admin/auth/roles";

    private static readonly TimeSpan LocalLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(30);

    // Three calls each waited at least the 500 ms command timeout before the fix (a queued command, then the timeout checked on a
    // one-second heartbeat): 1.5 s at the very least. With Valkey refusing connections they now fail at once.
    private static readonly TimeSpan ThreeCallsCeiling = TimeSpan.FromSeconds(1.2);

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await valkey.StartAsync(Ct);
    }

    [Fact]
    public async Task Valkey_stopped_the_api_still_checks_permissions_from_the_database()
    {
        await SignInAsync(AuthPermissions.RoleView);
        await using var host = RedisTestHosts.Create(Factory, valkey);
        using var client = await CreateClientAsync(host);
        using (var warm = await client.GetAsync(RolesRoute, Ct))
        {
            warm.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        await valkey.StopAsync(Ct);
        try
        {
            // Past the local lifetime the set must be loaded again, and Redis is gone: it comes from the database.
            Factory.Time.Advance(LocalLifetime);
            using var allowed = await client.GetAsync(RolesRoute, Ct);
            using var forbidden = await client.GetAsync(ProtectedTestEndpointStartupFilter.PermissionRoute, Ct);

            allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
            forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

            // Once more: the failed Redis calls open the circuit (the write of a loaded set may still be failing in the background
            // when the response is sent), and the answers stay the same.
            Factory.Time.Advance(LocalLifetime);
            using var again = await client.GetAsync(RolesRoute, Ct);

            again.StatusCode.ShouldBe(HttpStatusCode.OK);
            var availability = host.Services.GetRequiredService<RedisAvailability>();
            await RedisTestHosts.EventuallyAsync(() => Task.FromResult(!availability.IsAvailable), "the circuit to open", Ct);
        }
        finally
        {
            await valkey.StartAsync(Ct);
        }
    }

    [Fact]
    public async Task Valkey_stopped_readiness_is_Degraded_not_Unhealthy()
    {
        await valkey.StopAsync(Ct);
        try
        {
            // Redis is down before the host starts, too: the host starts anyway (abortConnect=false).
            await using var host = RedisTestHosts.Create(Factory, valkey);
            using var client = host.CreateClient();

            using var response = await client.GetAsync("/health/ready", Ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Degraded");
        }
        finally
        {
            await valkey.StartAsync(Ct);
        }
    }

    [Fact]
    public async Task Valkey_stopped_readiness_and_cache_reads_do_not_wait_for_Redis()
    {
        // A threshold the test never reaches keeps the circuit closed, so every call really goes to the stopped Valkey.
        await using var host = RedisTestHosts.Create(Factory, valkey, ("Caching:Redis:FailureThreshold", "100"));
        using var client = host.CreateClient();
        var cache = host.Services.GetRequiredService<IDistributedCache>();
        using (var healthy = await client.GetAsync("/health/ready", Ct))
        {
            (await healthy.Content.ReadAsStringAsync(Ct)).ShouldBe("Healthy");
        }

        await valkey.StopAsync(Ct);
        try
        {
            await RedisTestHosts.WaitUntilDisconnectedAsync(host, Ct);

            var reads = Stopwatch.StartNew();
            for (var read = 0; read < 3; read++)
            {
                (await cache.GetAsync($"auth:perm:{Guid.NewGuid():D}", Ct)).ShouldBeNull();
            }

            reads.Stop();

            var checks = Stopwatch.StartNew();
            for (var check = 0; check < 3; check++)
            {
                using var response = await client.GetAsync("/health/ready", Ct);
                response.StatusCode.ShouldBe(HttpStatusCode.OK);
                (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Degraded");
            }

            checks.Stop();

            reads.Elapsed.ShouldBeLessThan(ThreeCallsCeiling);
            checks.Elapsed.ShouldBeLessThan(ThreeCallsCeiling);
        }
        finally
        {
            await valkey.StartAsync(Ct);
        }
    }

    [Fact]
    public async Task Valkey_down_at_start_the_cache_works_from_the_database_then_through_Redis_once_it_is_back()
    {
        var users = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            users.Add((await AuthTestHarness.SignInAsync(Factory.Services, [AuthPermissions.RoleView], AuthTestHarness.DefaultLocale, Ct)).UserId);
        }

        await valkey.StopAsync(Ct);
        WebApplicationFactory<Program>? hostA = null;
        try
        {
            // The host starts with Redis down and checks permissions from the database.
            hostA = RedisTestHosts.Create(Factory, valkey);
            var (checkerA, readsA) = PermissionCheckers.Recording(hostA.Services);
            (await checkerA.HasPermissionAsync(users[0], AuthPermissions.RoleView, Ct)).ShouldBeTrue();
            (await checkerA.HasPermissionAsync(users[1], AuthPermissions.RoleView, Ct)).ShouldBeTrue();
            readsA.OpenedCount.ShouldBe(2);
        }
        finally
        {
            await valkey.StartAsync(Ct);
        }

        await using (hostA)
        {
            await RedisTestHosts.WaitUntilConnectedAsync(hostA, Ct);
            Factory.Time.Advance(BreakDuration);
            var (checkerA, _) = PermissionCheckers.Recording(hostA.Services);

            (await checkerA.HasPermissionAsync(users[2], AuthPermissions.RoleView, Ct)).ShouldBeTrue();
            await RedisTestHosts.EventuallyAsync(() => RedisTestHosts.HoldsPermissionsOfAsync(hostA, users[2]), "the set in Redis", Ct);

            // Through Redis: another instance gets the set without reading the database.
            await using var hostB = RedisTestHosts.Create(Factory, valkey);
            var (checkerB, readsB) = PermissionCheckers.Recording(hostB.Services);
            (await checkerB.HasPermissionAsync(users[2], AuthPermissions.RoleView, Ct)).ShouldBeTrue();
            readsB.OpenedCount.ShouldBe(0);
        }
    }

    [Fact]
    public async Task Valkey_back_the_cache_recovers_after_the_break()
    {
        var users = new List<Guid>();
        for (var index = 0; index < 5; index++)
        {
            users.Add((await AuthTestHarness.SignInAsync(Factory.Services, [AuthPermissions.RoleView], AuthTestHarness.DefaultLocale, Ct)).UserId);
        }

        await using var host = RedisTestHosts.Create(Factory, valkey);
        var (checker, reads) = PermissionCheckers.Recording(host.Services);
        var availability = host.Services.GetRequiredService<RedisAvailability>();
        (await checker.HasPermissionAsync(users[0], AuthPermissions.RoleView, Ct)).ShouldBeTrue();
        await RedisTestHosts.EventuallyAsync(() => RedisTestHosts.HoldsPermissionsOfAsync(host, users[0]), "the set in Redis", Ct);

        await valkey.StopAsync(Ct);
        try
        {
            // Each new user is a read and a write that fail: three failures in a row open the circuit.
            (await checker.HasPermissionAsync(users[1], AuthPermissions.RoleView, Ct)).ShouldBeTrue();
            (await checker.HasPermissionAsync(users[2], AuthPermissions.RoleView, Ct)).ShouldBeTrue();
            await RedisTestHosts.EventuallyAsync(() => Task.FromResult(!availability.IsAvailable), "the circuit to open", Ct);
        }
        finally
        {
            await valkey.StartAsync(Ct);
        }

        await RedisTestHosts.WaitUntilConnectedAsync(host, Ct);

        // Redis answers again, but the circuit stays open until the break is over: the set is loaded and not written to Redis.
        (await checker.HasPermissionAsync(users[3], AuthPermissions.RoleView, Ct)).ShouldBeTrue();
        (await RedisTestHosts.HoldsPermissionsOfAsync(host, users[3])).ShouldBeFalse();

        Factory.Time.Advance(BreakDuration);

        (await checker.HasPermissionAsync(users[4], AuthPermissions.RoleView, Ct)).ShouldBeTrue();
        await RedisTestHosts.EventuallyAsync(() => RedisTestHosts.HoldsPermissionsOfAsync(host, users[4]), "the set in Redis", Ct);
        availability.IsAvailable.ShouldBeTrue();
        reads.OpenedCount.ShouldBe(5);
    }
}
