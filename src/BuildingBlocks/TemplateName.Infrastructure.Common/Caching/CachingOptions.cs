namespace TemplateName.Infrastructure.Common.Caching;

/// <summary>The cache provider (section <c>Caching</c>; ADR 0027). Redis has its own settings under <c>Caching:Redis</c>.</summary>
public sealed class CachingOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Caching";

    /// <summary><c>memory</c> (the default), <c>redis</c> or <c>none</c>.</summary>
    public CachingProvider Provider { get; set; } = CachingProvider.Memory;
}
