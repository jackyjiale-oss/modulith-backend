using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StackExchange.Redis;
using TemplateName.Platform.Redis;
using TemplateName.UnitTests.Application;

namespace TemplateName.UnitTests.Platform;

/// <summary>The circuit every Redis caller shares: when it opens, how long it stays open, and what it logs.</summary>
public sealed class RedisAvailabilityTests
{
    private static readonly TimeSpan BreakDuration = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly RecordingLogger<RedisAvailability> _logger = new();

    [Fact]
    public void Opens_at_the_failure_threshold_and_not_before()
    {
        var availability = CreateAvailability();

        availability.RecordFailure(Failure());
        availability.RecordFailure(Failure());
        availability.IsAvailable.ShouldBeTrue();

        availability.RecordFailure(Failure());
        availability.IsAvailable.ShouldBeFalse();

        _time.Advance(BreakDuration);
        availability.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public void One_warning_is_logged_per_outage_window()
    {
        var availability = CreateAvailability();

        // First outage: the circuit opens, a call after the break fails and opens it again, and more failures follow. One warning.
        RecordFailures(availability, 3);
        _time.Advance(BreakDuration);
        RecordFailures(availability, 5);
        _time.Advance(BreakDuration);
        RecordFailures(availability, 1);
        Warnings().Count.ShouldBe(1);

        // A success ends the window; the next outage gets its own warning.
        _time.Advance(BreakDuration);
        availability.RecordSuccess();
        availability.RecordSuccess();
        RecordFailures(availability, 4);

        var warnings = Warnings();
        warnings.Count.ShouldBe(2);
        warnings[0].Message.ShouldContain("Redis");
        warnings[0].Message.ShouldContain("00:00:30");
    }

    [Fact]
    public void Failures_below_the_threshold_log_no_warning()
    {
        var availability = CreateAvailability();

        RecordFailures(availability, 2);
        availability.RecordSuccess();
        RecordFailures(availability, 2);

        Warnings().ShouldBeEmpty();
        availability.IsAvailable.ShouldBeTrue();
    }

    [Fact]
    public void A_success_after_an_outage_logs_that_Redis_is_available_again()
    {
        var availability = CreateAvailability();
        RecordFailures(availability, 3);
        _time.Advance(BreakDuration);

        availability.RecordSuccess();

        _logger.Entries.Count(entry => entry.Level == LogLevel.Information).ShouldBe(1);
        availability.IsAvailable.ShouldBeTrue();
    }

    private RedisAvailability CreateAvailability() => new(_time, Options.Create(new RedisCachingOptions()), _logger);

    private static void RecordFailures(RedisAvailability availability, int count)
    {
        for (var failure = 0; failure < count; failure++)
        {
            availability.RecordFailure(Failure());
        }
    }

    private List<(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Properties)> Warnings()
        => [.. _logger.Entries.Where(entry => entry.Level == LogLevel.Warning)];

    private static RedisConnectionException Failure() => new(ConnectionFailureType.UnableToConnect, CommandFlags.None, "No connection is available.", innerException: null, CommandStatus.Unknown);
}
