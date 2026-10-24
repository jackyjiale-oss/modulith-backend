using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TemplateName.Infrastructure.Common.Caching;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application;

namespace TemplateName.IntegrationTests.Caching;

/// <summary><c>Caching:Provider</c> on the real host: <c>memory</c> (the default) needs no Redis, and <c>none</c> turns the cache off.</summary>
public sealed class CachingProviderTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    [Fact]
    public async Task Provider_none_makes_every_permission_read_hit_the_database()
    {
        var user = await SignInAsync(AuthPermissions.RoleView);
        await using var host = Factory.WithWebHostBuilder(builder => builder.UseSetting("Caching:Provider", "none"));

        // The checker passes its own entry options (30 s in both layers); the provider's default flags still turn both layers off.
        var (checker, reads) = PermissionCheckers.Recording(host.Services);
        for (var read = 1; read <= 3; read++)
        {
            (await checker.HasPermissionAsync(user.UserId, AuthPermissions.RoleView, Ct)).ShouldBeTrue();
            reads.OpenedCount.ShouldBe(read);
        }

        (await checker.GetPermissionsAsync(user.UserId, Ct)).ShouldBe([AuthPermissions.RoleView]);
        reads.OpenedCount.ShouldBe(4);
    }

    [Fact]
    public async Task Default_memory_provider_needs_no_Redis()
    {
        Factory.Services.GetRequiredService<IOptions<CachingOptions>>().Value.Provider.ShouldBe(CachingProvider.Memory);
        Factory.Services.GetService<IConnectionMultiplexer>().ShouldBeNull();
        Factory.Services.GetService<IDistributedCache>().ShouldBeNull();
        Factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .ShouldNotContain(registration => registration.Name == "redis");

        using var response = await Client.GetAsync("/health/ready", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Healthy");
    }
}
