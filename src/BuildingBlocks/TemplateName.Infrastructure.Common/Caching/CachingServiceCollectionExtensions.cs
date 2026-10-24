using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace TemplateName.Infrastructure.Common.Caching;

internal static class CachingServiceCollectionExtensions
{
    private const HybridCacheEntryFlags BothLayersDisabled = HybridCacheEntryFlags.DisableLocalCache | HybridCacheEntryFlags.DisableDistributedCache;

    /// <summary>
    /// Binds <see cref="CachingOptions"/> (validated on start) and applies the provider to <see cref="HybridCacheOptions"/>: with
    /// <see cref="CachingProvider.None"/> the default entry options disable both cache layers, so every entry, also one whose caller passes
    /// its own expirations, is read from its source each time. (HybridCache uses the default flags for any entry options without flags of
    /// their own, which is every entry options in this solution.) With <see cref="CachingProvider.Redis"/> the host must have registered
    /// the distributed cache (<c>AddPlatformRedis</c>); a host without it fails on start instead of silently caching in memory only.
    /// </summary>
    internal static IServiceCollection AddCaching(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CachingOptions>()
            .Bind(configuration.GetSection(CachingOptions.SectionName))
            .Validate(options => Enum.IsDefined(options.Provider), "Caching:Provider must be memory, redis or none.")
            .Validate<IServiceProviderIsService>(
                (options, registrations) => options.Provider != CachingProvider.Redis || registrations.IsService(typeof(IDistributedCache)),
                "Caching:Provider is redis, but no distributed cache is registered: the host must call AddPlatformRedis (TemplateName.Platform.Redis).")
            .ValidateOnStart();

        services.AddOptions<HybridCacheOptions>().PostConfigure<IOptions<CachingOptions>>((options, caching) =>
        {
            if (caching.Value.Provider != CachingProvider.None)
            {
                return;
            }

            var defaults = options.DefaultEntryOptions;
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = defaults?.Expiration,
                LocalCacheExpiration = defaults?.LocalCacheExpiration,
                Flags = (defaults?.Flags ?? HybridCacheEntryFlags.None) | BothLayersDisabled,
            };
        });

        return services;
    }
}
