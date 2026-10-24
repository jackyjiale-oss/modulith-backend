using System.Threading.RateLimiting;

namespace TemplateName.Web.Common.Security;

/// <summary>
/// The default <see cref="IFixedWindowLimiterFactory"/>: a <see cref="FixedWindowRateLimiter"/> per partition, counted in memory by this
/// instance alone (so N instances allow N times the limit), with no queue and replenished automatically at the end of each window. A
/// refusal carries <c>Retry-After</c> metadata of one window.
/// </summary>
internal sealed class InMemoryFixedWindowLimiterFactory : IFixedWindowLimiterFactory
{
    public RateLimiter Create(string policy, string partitionKey, int permitLimit, TimeSpan window)
        => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
}
