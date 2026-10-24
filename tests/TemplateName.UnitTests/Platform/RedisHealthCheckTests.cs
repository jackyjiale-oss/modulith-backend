using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;
using TemplateName.Platform.Redis;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Platform;

/// <summary>The readiness check <c>redis</c>: never longer than the command timeout, and no call to Redis during a break.</summary>
public sealed class RedisHealthCheckTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMilliseconds(500);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly IConnectionMultiplexer _connection = Substitute.For<IConnectionMultiplexer>();
    private readonly IDatabase _database = Substitute.For<IDatabase>();
    private readonly RedisAvailability _availability;
    private readonly RedisHealthCheck _check;

    public RedisHealthCheckTests()
    {
        var options = Options.Create(new RedisCachingOptions());
        _connection.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(_database);
        _availability = new RedisAvailability(_time, options, new RecordingLogger<RedisAvailability>());
        _check = new RedisHealthCheck(_connection, _availability, _time, options);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_PING_answer_is_Healthy()
    {
        _database.PingAsync(Arg.Any<CommandFlags>()).Returns(Task.FromResult(TimeSpan.FromMilliseconds(2)));

        var result = await _check.CheckHealthAsync(Context(), Ct);

        result.Status.ShouldBe(HealthStatus.Healthy);
    }

    [Fact]
    public async Task A_PING_that_does_not_answer_is_Degraded_after_the_command_timeout_and_not_later()
    {
        var never = new TaskCompletionSource<TimeSpan>();
        _database.PingAsync(Arg.Any<CommandFlags>()).Returns(never.Task);

        var check = _check.CheckHealthAsync(Context(), Ct);
        _time.Advance(CommandTimeout - TimeSpan.FromMilliseconds(1));
        check.IsCompleted.ShouldBeFalse();

        _time.Advance(TimeSpan.FromMilliseconds(1));
        (await check).Status.ShouldBe(HealthStatus.Degraded);
    }

    [Fact]
    public async Task During_a_break_it_is_Degraded_at_once_without_calling_Redis()
    {
        for (var failure = 0; failure < 3; failure++)
        {
            _availability.RecordFailure(new InvalidOperationException("Redis is down."));
        }

        var result = await _check.CheckHealthAsync(Context(), Ct);

        result.Status.ShouldBe(HealthStatus.Degraded);
        _connection.DidNotReceiveWithAnyArgs().GetDatabase();
    }

    [Fact]
    public async Task After_the_break_it_pings_again_without_taking_the_cache_probe()
    {
        for (var failure = 0; failure < 3; failure++)
        {
            _availability.RecordFailure(new InvalidOperationException("Redis is down."));
        }

        _time.Advance(TimeSpan.FromSeconds(30));
        _database.PingAsync(Arg.Any<CommandFlags>()).Returns(Task.FromResult(TimeSpan.FromMilliseconds(2)));

        (await _check.CheckHealthAsync(Context(), Ct)).Status.ShouldBe(HealthStatus.Healthy);
        _availability.TryBeginCall().ShouldBeTrue();
    }

    private static HealthCheckContext Context() => new()
    {
        Registration = new HealthCheckRegistration("redis", Substitute.For<IHealthCheck>(), HealthStatus.Degraded, ["ready"]),
    };
}
