using Microsoft.Extensions.Caching.Hybrid;

namespace TemplateName.Modules.Auth.Infrastructure.Authorization;

/// <summary>
/// The cache entries of the permission sets: one per user, under <c>auth:perm:{userId}</c> (<c>{module}:{entity}:{id}</c>; with Redis
/// the distributed key also carries <c>Caching:Redis:InstanceName</c> in front), kept for 30 seconds (ADR 0016, ADR 0027). The entry
/// options set no <c>Flags</c>, so <c>Caching:Provider = none</c> (the default flags) still turns both layers off for them.
/// </summary>
internal static class PermissionCache
{
    /// <summary>
    /// How long a loaded permission set is used before it is read again, in memory and in Redis alike. It also bounds how long another
    /// instance can serve a set that was invalidated here: removal clears Redis and this instance's memory, not the others' (ADR 0027).
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    public static readonly HybridCacheEntryOptions EntryOptions = new()
    {
        Expiration = Lifetime,
        LocalCacheExpiration = Lifetime,
    };

    public static string Key(Guid userId) => $"auth:perm:{userId:D}";
}
