using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using TemplateName.Web.Common.Security;

namespace TemplateName.Platform.Redis;

/// <summary>
/// The <see cref="IFixedWindowLimiterFactory"/> of <c>Caching:Provider = redis</c>, registered by <c>AddPlatformRedis</c> in place of the
/// in-memory default: each partition gets a <see cref="RedisFixedWindowLimiter"/> on the key
/// <c>{InstanceName}rl:{policy}:{partitionKey}</c> (for example <c>TemplateName:rl:auth-strict:ip:203.0.113.7</c>), on the instance's
/// shared connection and <see cref="RedisAvailability"/> circuit.
/// </summary>
internal sealed class RedisFixedWindowLimiterFactory(
    IConnectionMultiplexer connection,
    RedisAvailability availability,
    TimeProvider timeProvider,
    IOptions<RedisCachingOptions> options) : IFixedWindowLimiterFactory
{
    private readonly RedisCachingOptions _options = options.Value;

    public RateLimiter Create(string policy, string partitionKey, int permitLimit, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrEmpty(policy);
        ArgumentNullException.ThrowIfNull(partitionKey);

        return new RedisFixedWindowLimiter(
            connection.GetDatabase(),
            $"{_options.InstanceName}rl:{policy}:{partitionKey}",
            permitLimit,
            window,
            availability,
            timeProvider,
            _options.CommandTimeout);
    }
}
