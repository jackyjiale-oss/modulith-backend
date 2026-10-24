using System.ComponentModel.DataAnnotations;

namespace TemplateName.Platform.Redis;

/// <summary>
/// Redis as the shared second level of the cache (section <c>Caching:Redis</c>; ADR 0027), read only when <c>Caching:Provider</c> is
/// <c>redis</c> and validated on start then. The server is <c>ConnectionStrings:Redis</c>.
/// </summary>
public sealed class RedisCachingOptions
{
    internal const string SectionName = "Caching:Redis";

    /// <summary>
    /// The prefix of every key this application writes, so applications can share one server: a key is
    /// <c>{InstanceName}{module}:{entity}:{id}</c>, for example <c>TemplateName:auth:perm:{userId}</c>. The SignalR backplane's channels
    /// start with <c>{InstanceName}signalr</c>.
    /// </summary>
    [Required]
    public string InstanceName { get; set; } = "TemplateName:";

    /// <summary>Consecutive failed Redis calls after which Redis is skipped for <see cref="BreakDuration"/>.</summary>
    [Range(1, 100)]
    public int FailureThreshold { get; set; } = 3;

    /// <summary>How long Redis is skipped (a read is a miss, a write is dropped) once <see cref="FailureThreshold"/> is reached.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The connect, synchronous and asynchronous timeouts of the Redis connection: the most a request waits for a Redis that does not
    /// answer, before the call counts as a failure.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00.050", "00:00:30")]
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMilliseconds(500);
}
