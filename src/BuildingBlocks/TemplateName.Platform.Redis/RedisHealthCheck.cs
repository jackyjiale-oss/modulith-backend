using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace TemplateName.Platform.Redis;

/// <summary>
/// The readiness check <c>redis</c>, registered with the failure status <see cref="HealthStatus.Degraded"/>, so a Redis outage makes
/// <c>/health/ready</c> answer 200 <c>Degraded</c>, not 503: the instance still serves (the cache misses to the database) and a load
/// balancer must not drain every instance for a cache outage. During a break of the <see cref="RedisAvailability"/> circuit it answers
/// at once without calling Redis; otherwise (closed, or half-open after the break) it sends one <c>PING</c> on the shared connection
/// and gives up after <see cref="RedisCachingOptions.CommandTimeout"/>, so the check never takes longer than that. It does not report
/// to the circuit and does not take its probe: readiness follows Redis, the cache follows its own calls.
/// </summary>
internal sealed class RedisHealthCheck(
    IConnectionMultiplexer connection,
    RedisAvailability availability,
    TimeProvider timeProvider,
    IOptions<RedisCachingOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (availability.IsInBreak)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis is skipped: its circuit is open after repeated failures.");
        }

        try
        {
            var latency = await connection.GetDatabase().PingAsync().WaitAsync(options.Value.CommandTimeout, timeProvider, cancellationToken);
            return HealthCheckResult.Healthy($"Redis answered PING in {latency.TotalMilliseconds:0} ms.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis did not answer PING in time.", exception);
        }
    }
}
