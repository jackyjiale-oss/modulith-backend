using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace TemplateName.Platform.Redis;

/// <summary>
/// The readiness check <c>redis</c>: one <c>PING</c> on the shared connection, within the command timeout. Registered with the failure
/// status <see cref="HealthStatus.Degraded"/>, so a Redis outage makes <c>/health/ready</c> answer 200 <c>Degraded</c>, not 503: the
/// instance still serves (the cache misses to the database) and a load balancer must not drain every instance for a cache outage. It asks
/// Redis itself, not <see cref="RedisAvailability"/>, so it sees Redis come back while the cache's circuit is still open.
/// </summary>
internal sealed class RedisHealthCheck(IConnectionMultiplexer connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            var latency = await connection.GetDatabase().PingAsync().WaitAsync(cancellationToken);
            return HealthCheckResult.Healthy($"Redis answered PING in {latency.TotalMilliseconds:0} ms.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis did not answer PING.", exception);
        }
    }
}
