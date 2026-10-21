using System.ComponentModel.DataAnnotations;

namespace TemplateName.Platform.Redis;

/// <summary>
/// The Redis server (<c>ConnectionStrings:Redis</c>, a StackExchange.Redis configuration string such as <c>localhost:6379</c>). Bound and
/// validated on start only when <c>Caching:Provider</c> is <c>redis</c>. Supplied by user secrets or the environment, never by appsettings
/// files, when it carries a password.
/// </summary>
internal sealed class RedisConnectionStringOptions
{
    internal const string SectionName = "ConnectionStrings";

    [Required(ErrorMessage = "ConnectionStrings:Redis is required when Caching:Provider is redis.")]
    public string Redis { get; set; } = string.Empty;
}
