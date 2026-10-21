namespace TemplateName.Infrastructure.Common.Caching;

/// <summary>Where <c>HybridCache</c> keeps its entries (<c>Caching:Provider</c>; the value is read without regard to case).</summary>
public enum CachingProvider
{
    /// <summary>In memory, per instance (the default). Nothing else is needed.</summary>
    Memory,

    /// <summary>In memory per instance, with Redis as the shared second level. Needs <c>TemplateName.Platform.Redis</c>.</summary>
    Redis,

    /// <summary>No cache at all, in memory or elsewhere: every read goes to its source. For diagnosing, never for production.</summary>
    None,
}
