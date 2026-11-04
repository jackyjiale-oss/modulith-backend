using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Caching;

/// <summary>
/// <c>Caching:Provider = redis</c> in a project generated with <c>--caching memory</c>, which has no Redis project: the host refuses to
/// start and names the registration it misses, instead of caching in memory only. The template generates this file only for that value;
/// in the template repository, where the Redis project is referenced, the test skips.
/// </summary>
public sealed class CachingProviderWithoutRedisTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private const string RedisOptionsType = "TemplateName.Platform.Redis.RedisCachingOptions, TemplateName.Platform.Redis";

    [Fact]
    public void Redis_provider_without_the_project_fails_at_start()
    {
        Assert.SkipWhen(Type.GetType(RedisOptionsType) is not null, "The Redis project is referenced.");
        using var configured = Factory.WithWebHostBuilder(builder => builder.UseSetting("Caching:Provider", "redis"));

        var exception = Should.Throw<OptionsValidationException>(() => configured.CreateClient());

        exception.Message.ShouldContain("AddPlatformRedis");
    }
}
