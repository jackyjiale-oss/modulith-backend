using Microsoft.Extensions.Caching.Hybrid;

namespace TemplateName.Modules.Auth.Infrastructure.Authorization;

/// <summary>The cache entries of the permission sets: one per user, under <c>perm:{userId}</c>, kept for 30 seconds (ADR 0016).</summary>
internal static class PermissionCache
{
    /// <summary>How long a loaded permission set is used before it is read again, in memory and in a distributed cache alike.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    public static readonly HybridCacheEntryOptions EntryOptions = new()
    {
        Expiration = Lifetime,
        LocalCacheExpiration = Lifetime,
    };

    public static string Key(Guid userId) => $"perm:{userId:D}";
}
