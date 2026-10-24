using System.Threading.RateLimiting;

namespace TemplateName.Web.Common.Security;

/// <summary>
/// Creates the fixed-window limiter of one partition of a rate-limit policy. The <c>global</c>, <c>auth-strict</c> and
/// <c>notifications-hub-tickets</c> policies get every partition's limiter here, so where the count lives is pluggable: the default
/// (registered by <c>AddHttpSecurity</c> unless another one is) counts in memory per instance, and <c>AddPlatformRedis</c> replaces it
/// with a limiter whose count is shared by every instance through Redis (D5, ADR 0027).
/// <para>
/// The ASP.NET Core rate limiter keeps one limiter per partition key while the partition is in use and disposes it when it has been idle
/// for a while, so an implementation creates something cheap and owns only what it creates.
/// </para>
/// <para>
/// A caller other than that middleware must acquire with <c>AcquireAsync</c>. The Redis limiter's synchronous <c>AttemptAcquire</c>
/// cannot wait for Redis, so while Redis can be called it refuses without counting: right for the middleware, which calls
/// <c>AttemptAcquire</c> first and <c>AcquireAsync</c> after a refusal, but a caller that used only <c>AttemptAcquire</c> would be
/// refused every time.
/// </para>
/// </summary>
public interface IFixedWindowLimiterFactory
{
    /// <summary>
    /// A limiter allowing <paramref name="permitLimit"/> permits per fixed <paramref name="window"/> for <paramref name="partitionKey"/>
    /// (for example <c>ip:203.0.113.7</c>, <c>ip:::1</c> or <c>user:{sub}</c>) of <paramref name="policy"/> (for example
    /// <c>auth-strict</c>). Requests are never queued: one beyond the limit is refused at once, with <c>Retry-After</c> metadata.
    /// </summary>
    RateLimiter Create(string policy, string partitionKey, int permitLimit, TimeSpan window);
}
