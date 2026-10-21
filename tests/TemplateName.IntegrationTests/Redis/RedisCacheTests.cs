using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Data;
using TemplateName.ArchitectureTests;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.IntegrationTests.Redis;

/// <summary>
/// The permission cache with <c>Caching:Provider = redis</c>: two hosts (two API instances) on one SQL database, one clock and one Valkey.
/// Redis is the shared second level; each host keeps its own first level for 30 seconds (spec 3.3).
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisCacheTests(IntegrationTestWebAppFactory factory, ValkeyFixture valkey) : IntegrationTestBase(factory)
{
    private static readonly TimeSpan LocalLifetime = TimeSpan.FromSeconds(30);

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        await valkey.StartAsync(Ct);
    }

    [Fact]
    public async Task A_permission_set_cached_by_host_A_is_served_to_host_B_without_a_database_read()
    {
        var user = await SignInAsync(AuthPermissions.RoleView);
        await using var hostA = RedisTestHosts.Create(Factory, valkey);
        await using var hostB = RedisTestHosts.Create(Factory, valkey);
        var (checkerA, readsA) = PermissionCheckers.Recording(hostA.Services);
        var (checkerB, readsB) = PermissionCheckers.Recording(hostB.Services);

        (await checkerA.HasPermissionAsync(user.UserId, AuthPermissions.RoleView, Ct)).ShouldBeTrue();
        readsA.OpenedCount.ShouldBe(1);
        await RedisTestHosts.EventuallyAsync(() => RedisTestHosts.HoldsPermissionsOfAsync(hostA, user.UserId), "the set in Redis", Ct);

        (await checkerB.HasPermissionAsync(user.UserId, AuthPermissions.RoleView, Ct)).ShouldBeTrue();
        (await checkerB.GetPermissionsAsync(user.UserId, Ct)).ShouldBe([AuthPermissions.RoleView]);
        readsB.OpenedCount.ShouldBe(0);
    }

    [Fact]
    public async Task Invalidating_on_A_is_seen_by_B_after_the_thirty_second_L1_lifetime()
    {
        var user = await SignInAsync(AuthPermissions.RoleView);
        await using var hostA = RedisTestHosts.Create(Factory, valkey);
        await using var hostB = RedisTestHosts.Create(Factory, valkey);
        var (checkerA, _) = PermissionCheckers.Recording(hostA.Services);
        var (checkerB, _) = PermissionCheckers.Recording(hostB.Services);
        (await checkerA.HasPermissionAsync(user.UserId, AuthPermissions.RoleView, Ct)).ShouldBeTrue();
        await RedisTestHosts.EventuallyAsync(() => RedisTestHosts.HoldsPermissionsOfAsync(hostA, user.UserId), "the set in Redis", Ct);
        (await checkerB.HasPermissionAsync(user.UserId, AuthPermissions.RoleView, Ct)).ShouldBeTrue();

        // The user loses the permission and host A invalidates: Redis and A's own first level are cleared.
        await SuspendAsync(user.UserId);
        await checkerA.InvalidateUsersAsync([user.UserId], Ct);

        (await RedisTestHosts.HoldsPermissionsOfAsync(hostA, user.UserId)).ShouldBeFalse();
        (await checkerA.HasPermissionAsync(user.UserId, AuthPermissions.RoleView, Ct)).ShouldBeFalse();

        // B's first level is its own and nothing tells it: B serves the stale set until its 30 seconds end, and not a moment longer.
        Factory.Time.Advance(LocalLifetime - TimeSpan.FromMilliseconds(1));
        (await checkerB.HasPermissionAsync(user.UserId, AuthPermissions.RoleView, Ct)).ShouldBeTrue();

        Factory.Time.Advance(TimeSpan.FromMilliseconds(1));
        (await checkerB.HasPermissionAsync(user.UserId, AuthPermissions.RoleView, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Readiness_includes_the_redis_check_and_is_Healthy_while_Valkey_runs()
    {
        await using var host = RedisTestHosts.Create(Factory, valkey);
        using var client = host.CreateClient();

        using var response = await client.GetAsync("/health/ready", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Healthy");
    }

    [Fact]
    public void Docker_compose_pins_the_same_Valkey_image_as_the_tests()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docker-compose.yml"));

        compose.ShouldContain($"image: {ValkeyFixture.Image}");
        ValkeyFixture.Image.ShouldNotEndWith(":latest");
        ValkeyFixture.Image.ShouldStartWith("valkey/valkey:");
    }

    private async Task SuspendAsync(Guid userId)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var user = (await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(userId, Ct)).ShouldNotBeNull();
        user.Suspend(Factory.Time.GetUtcNow());
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
    }
}
