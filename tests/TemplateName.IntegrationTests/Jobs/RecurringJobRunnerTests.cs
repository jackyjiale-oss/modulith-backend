using System.Diagnostics.Metrics;
using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common;
using TemplateName.Infrastructure.Common.Idempotency;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.IntegrationTests.Infrastructure;

namespace TemplateName.IntegrationTests.Jobs;

public sealed class RecurringJobRunnerTests(IntegrationTestWebAppFactory factory) : IntegrationTestBase(factory)
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    private static readonly Func<string, bool> CountingOnly = name => name == CountingJob.JobName;

    private static readonly Func<string, bool> CountingAndThrowing = name => name is CountingJob.JobName or ThrowingJob.JobName;

    private DateTime Now => Factory.Time.GetUtcNow().UtcDateTime;

    private CountingJobProbe Probe => Factory.Services.GetRequiredService<CountingJobProbe>();

    private RecurringJobRunner Runner => Factory.Services.GetRequiredService<RecurringJobRunner>();

    private TimeSpan LeaseDuration => Factory.Services.GetRequiredService<IOptions<JobsOptions>>().Value.LeaseDuration;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Probe.Reset();
    }

    public override ValueTask DisposeAsync()
    {
        Probe.Reset();
        return base.DisposeAsync();
    }

    [Fact]
    public async Task A_due_job_runs_once_and_its_state_row_is_written()
    {
        var startedAt = Now;
        Probe.DuringRun = () => Factory.Time.Advance(TimeSpan.FromSeconds(3));

        (await Runner.RunDueAsync(CountingOnly, Ct)).ShouldBe(1);
        (await Runner.RunDueAsync(CountingOnly, Ct)).ShouldBe(0);

        Probe.RunCount.ShouldBe(1);
        var state = await StateAsync(CountingJob.JobName);
        state.LastStatus.ShouldBe("Succeeded");
        state.LastProcessed.ShouldBe(1);
        state.LastError.ShouldBeNull();
        state.LastStartedAt.ShouldBe(startedAt);
        state.LastFinishedAt.ShouldBe(startedAt.AddSeconds(3));
        state.NextRunAt.ShouldBe(startedAt.AddSeconds(3) + CountingJob.Interval);
        state.LockedUntil.ShouldBeNull();
    }

    [Fact]
    public async Task A_job_runs_again_once_its_interval_has_passed()
    {
        (await Runner.RunDueAsync(CountingOnly, Ct)).ShouldBe(1);

        Factory.Time.Advance(CountingJob.Interval - TimeSpan.FromSeconds(1));
        (await Runner.RunDueAsync(CountingOnly, Ct)).ShouldBe(0);

        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        (await Runner.RunDueAsync(CountingOnly, Ct)).ShouldBe(1);

        Probe.RunCount.ShouldBe(2);
    }

    [Fact]
    public async Task Two_runners_ticking_together_run_a_job_once()
    {
        var first = Runner;
        var second = RecurringJobRunner.Create(Factory.Services);
        var entered = Probe.HoldNextRun();

        Task<int>[] runs =
        [
            Task.Run(() => first.RunDueAsync(CountingOnly, Ct), Ct),
            Task.Run(() => second.RunDueAsync(CountingOnly, Ct), Ct),
        ];
        await entered.WaitAsync(WaitLimit, Ct);

        // One runner holds the lease and is parked inside the job; the other finds nothing it may claim.
        var skipped = await (await Task.WhenAny(runs).WaitAsync(WaitLimit, Ct));
        skipped.ShouldBe(0);
        (await StateAsync(CountingJob.JobName)).LockedUntil.ShouldBe(Now + LeaseDuration);

        Probe.Release();
        (await Task.WhenAll(runs).WaitAsync(WaitLimit, Ct)).Sum().ShouldBe(1);
        Probe.RunCount.ShouldBe(1);
        (await StateAsync(CountingJob.JobName)).LastStatus.ShouldBe("Succeeded");
    }

    [Fact]
    public async Task Two_runners_started_at_once_without_a_hold_run_a_job_once()
    {
        var first = Runner;
        var second = RecurringJobRunner.Create(Factory.Services);

        var runs = await Task.WhenAll(
            Task.Run(() => first.RunDueAsync(CountingOnly, Ct), Ct),
            Task.Run(() => second.RunDueAsync(CountingOnly, Ct), Ct));

        runs.Sum().ShouldBe(1);
        Probe.RunCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_throwing_job_is_recorded_Failed_and_does_not_stop_another_job()
    {
        // The throwing job is the most overdue, so it is claimed and fails first.
        await SeedStateAsync(ThrowingJob.JobName, nextRunAt: Now.AddMinutes(-1));
        await SeedStateAsync(CountingJob.JobName, nextRunAt: Now);

        (await Runner.RunDueAsync(CountingAndThrowing, Ct)).ShouldBe(2);

        var failed = await StateAsync(ThrowingJob.JobName);
        failed.LastStatus.ShouldBe("Failed");
        failed.LastError!.ShouldContain(ThrowingJob.ErrorMessage);
        failed.LastProcessed.ShouldBeNull();
        failed.LastFinishedAt.ShouldBe(Now);
        failed.NextRunAt.ShouldBe(Now + ThrowingJob.Interval);
        failed.LockedUntil.ShouldBeNull();

        Probe.RunCount.ShouldBe(1);
        (await StateAsync(CountingJob.JobName)).LastStatus.ShouldBe("Succeeded");
    }

    [Fact]
    public async Task A_failed_job_that_then_succeeds_clears_its_error()
    {
        await SeedStateAsync(CountingJob.JobName, nextRunAt: Now, lastStatus: "Failed", lastError: "An earlier failure.");

        (await Runner.RunDueAsync(CountingOnly, Ct)).ShouldBe(1);

        var state = await StateAsync(CountingJob.JobName);
        state.LastStatus.ShouldBe("Succeeded");
        state.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task A_job_that_outlives_its_lease_is_cancelled_and_recorded_Failed()
    {
        var startedAt = Now;
        var entered = Probe.HoldNextRun();
        var run = Runner.RunDueAsync(CountingOnly, Ct);
        await entered.WaitAsync(WaitLimit, Ct);

        Factory.Time.Advance(LeaseDuration - TimeSpan.FromSeconds(1));
        run.IsCompleted.ShouldBeFalse();

        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        (await run.WaitAsync(WaitLimit, Ct)).ShouldBe(1);

        var state = await StateAsync(CountingJob.JobName);
        state.LastStatus.ShouldBe("Failed");
        state.LastError!.ShouldContain("lease");
        state.LastStartedAt.ShouldBe(startedAt);
        state.LastFinishedAt.ShouldBe(startedAt + LeaseDuration);
        state.NextRunAt.ShouldBe(startedAt + LeaseDuration + CountingJob.Interval);
        state.LockedUntil.ShouldBeNull();
    }

    [Fact]
    public async Task A_runner_whose_lease_expired_does_not_overwrite_the_runner_that_reclaimed_the_job()
    {
        var reclaiming = RecurringJobRunner.Create(Factory.Services);

        // The first run ignores its cancelled token and stays inside the job past its lease.
        var entered = Probe.HoldNextRun(ignoresCancellation: true);
        var late = Runner.RunDueAsync(CountingOnly, Ct);
        await entered.WaitAsync(WaitLimit, Ct);

        Factory.Time.Advance(LeaseDuration + TimeSpan.FromSeconds(1));
        var reclaimedAt = Now;
        (await reclaiming.RunDueAsync(CountingOnly, Ct)).ShouldBe(1);

        // The late run now finishes successfully, but its lease is gone, so the second run's outcome stands.
        Factory.Time.Advance(TimeSpan.FromMinutes(1));
        Probe.Release();
        (await late.WaitAsync(WaitLimit, Ct)).ShouldBe(1);

        Probe.RunCount.ShouldBe(2);
        var state = await StateAsync(CountingJob.JobName);
        state.LastStatus.ShouldBe("Succeeded");
        state.LastProcessed.ShouldBe(2);
        state.LastStartedAt.ShouldBe(reclaimedAt);
        state.NextRunAt.ShouldBe(reclaimedAt + CountingJob.Interval);
        state.LockedUntil.ShouldBeNull();
    }

    [Fact]
    public async Task A_crashed_runners_lease_is_reclaimed_after_it_expires()
    {
        var lockedUntil = Now.AddMinutes(5);
        await SeedStateAsync(CountingJob.JobName, nextRunAt: Now, lockedUntil: lockedUntil);

        (await Runner.RunDueAsync(CountingOnly, Ct)).ShouldBe(0);

        Factory.Time.Advance(TimeSpan.FromMinutes(5));
        (await Runner.RunDueAsync(CountingOnly, Ct)).ShouldBe(0);

        Factory.Time.Advance(TimeSpan.FromSeconds(1));
        (await Runner.RunDueAsync(CountingOnly, Ct)).ShouldBe(1);

        Probe.RunCount.ShouldBe(1);
        var state = await StateAsync(CountingJob.JobName);
        state.LastStatus.ShouldBe("Succeeded");
        state.LockedUntil.ShouldBeNull();
    }

    [Fact]
    public async Task A_missed_interval_runs_once_not_in_a_burst()
    {
        (await Runner.RunDueAsync(CountingOnly, Ct)).ShouldBe(1);

        Factory.Time.Advance(CountingJob.Interval * 10);
        (await Runner.RunDueAsync(CountingOnly, Ct)).ShouldBe(1);
        (await Runner.RunDueAsync(CountingOnly, Ct)).ShouldBe(0);

        Probe.RunCount.ShouldBe(2);
        (await StateAsync(CountingJob.JobName)).NextRunAt.ShouldBe(Now + CountingJob.Interval);
    }

    [Fact]
    public async Task A_disabled_job_never_runs()
    {
        await using var host = Factory.WithWebHostBuilder(builder => builder.UseSetting($"Jobs:{CountingJob.JobName}:Enabled", "false"));
        var runner = host.Services.GetRequiredService<RecurringJobRunner>();

        (await runner.RunDueAsync(CountingAndThrowing, Ct)).ShouldBe(1);
        Factory.Time.Advance(TimeSpan.FromDays(2));
        (await runner.RunDueAsync(CountingAndThrowing, Ct)).ShouldBe(1);

        Probe.RunCount.ShouldBe(0);
        (await FindStateAsync(CountingJob.JobName)).ShouldBeNull();
        (await StateAsync(ThrowingJob.JobName)).LastStatus.ShouldBe("Failed");
    }

    [Fact]
    public async Task A_job_interval_from_configuration_replaces_the_default()
    {
        await using var host = Factory.WithWebHostBuilder(builder => builder.UseSetting($"Jobs:{CountingJob.JobName}:Interval", "00:05:00"));
        var runner = host.Services.GetRequiredService<RecurringJobRunner>();

        (await runner.RunDueAsync(CountingOnly, Ct)).ShouldBe(1);

        (await StateAsync(CountingJob.JobName)).NextRunAt.ShouldBe(Now.AddMinutes(5));
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("12 hours")]
    public async Task An_invalid_job_interval_stops_the_host_from_starting_and_names_the_job(string interval)
    {
        await using var host = Factory.WithWebHostBuilder(builder => builder.UseSetting($"Jobs:{CountingJob.JobName}:Interval", interval));

        var exception = Should.Throw<Exception>(() => host.Services);

        exception.ToString().ShouldContain($"Jobs:{CountingJob.JobName}");
        Probe.RunCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("12 hours")]
    public async Task A_job_whose_options_fail_at_run_time_is_skipped_and_the_others_still_run(string interval)
    {
        // The start-time check would stop such a host; a runner over options that went bad later must still run the other jobs.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"Jobs:{CountingJob.JobName}:Interval"] = interval })
            .Build();
        await using var badOptions = new ServiceCollection().AddLogging().AddInfrastructureCommon(configuration).BuildServiceProvider();
        var services = Factory.Services;
        var runner = new RecurringJobRunner(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetServices<RecurringJobRegistration>(),
            services.GetRequiredService<IOptions<JobsOptions>>(),
            badOptions.GetRequiredService<IOptionsMonitor<RecurringJobOptions>>(),
            services.GetRequiredService<RecurringJobMetrics>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<ILogger<RecurringJobRunner>>());

        (await runner.RunDueAsync(CountingAndThrowing, Ct)).ShouldBe(1);

        Probe.RunCount.ShouldBe(0);
        (await FindStateAsync(CountingJob.JobName)).ShouldBeNull();
        (await StateAsync(ThrowingJob.JobName)).LastStatus.ShouldBe("Failed");
    }

    [Fact]
    public async Task A_job_left_out_by_the_filter_is_not_run_and_gets_no_state_row()
    {
        (await Runner.RunDueAsync(name => name == ThrowingJob.JobName, Ct)).ShouldBe(1);

        Probe.RunCount.ShouldBe(0);
        (await FindStateAsync(CountingJob.JobName)).ShouldBeNull();
    }

    [Fact]
    public async Task Duplicate_job_names_fail_at_first_use()
    {
        var configuration = new ConfigurationBuilder().Build();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddInfrastructureCommon(configuration)
            .RemoveAll<RecurringJobRegistration>() // the built-in jobs need a database context; this registry holds only the test's own
            .AddSingleton(Probe)
            .AddRecurringJob<CountingJob>()
            .AddRecurringJob<SameNameAsCountingJob>()
            .BuildServiceProvider();
        var runner = services.GetRequiredService<RecurringJobRunner>();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => runner.RunDueAsync(include: null, Ct));
        exception.Message.ShouldContain(CountingJob.JobName);
        exception.Message.ShouldContain(nameof(SameNameAsCountingJob));

        // The registry is not half-built: the next use fails the same way.
        await Should.ThrowAsync<InvalidOperationException>(() => runner.RunDueAsync(include: null, Ct));
        Probe.RunCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_job_name_that_cannot_be_a_configuration_key_fails_at_first_use()
    {
        var configuration = new ConfigurationBuilder().Build();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddInfrastructureCommon(configuration)
            .RemoveAll<RecurringJobRegistration>() // the built-in jobs need a database context; this registry holds only the test's own
            .AddRecurringJob<DottedNameJob>()
            .BuildServiceProvider();
        var runner = services.GetRequiredService<RecurringJobRunner>();

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => runner.RunDueAsync(include: null, Ct));
        exception.Message.ShouldContain(DottedNameJob.JobName);
    }

    [Fact]
    public async Task Registering_the_same_job_type_twice_registers_it_once()
    {
        var configuration = new ConfigurationBuilder().Build();
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddInfrastructureCommon(configuration)
            .RemoveAll<RecurringJobRegistration>() // the built-in jobs need a database context; this registry holds only the test's own
            .AddRecurringJob<ThrowingJob>()
            .AddRecurringJob<ThrowingJob>()
            .BuildServiceProvider();

        services.GetServices<RecurringJobRegistration>().ShouldHaveSingleItem().JobType.ShouldBe(typeof(ThrowingJob));
    }

    [Fact]
    public async Task Metrics_record_one_run_per_outcome()
    {
        using var measurements = new PlatformMeasurements();

        (await Runner.RunDueAsync(CountingAndThrowing, Ct)).ShouldBe(2);

        measurements.Of("platform.jobs.runs").ShouldBe(
            [$"1 job={CountingJob.JobName} outcome=succeeded", $"1 job={ThrowingJob.JobName} outcome=failed"],
            ignoreOrder: true);
        measurements.Of("platform.jobs.processed").ShouldBe([$"1 job={CountingJob.JobName}"]);
        measurements.Of("platform.jobs.duration").Count.ShouldBe(2);
    }

    private async Task<JobStateRecord> StateAsync(string name)
        => await FindStateAsync(name) ?? throw new InvalidOperationException($"No state row for job {name}.");

    private async Task<JobStateRecord?> FindStateAsync(string name)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().JobStates
            .AsNoTracking()
            .SingleOrDefaultAsync(state => state.Name == name, Ct);
    }

    private async Task SeedStateAsync(
        string name,
        DateTime nextRunAt,
        DateTime? lockedUntil = null,
        string? lastStatus = null,
        string? lastError = null)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        context.JobStates.Add(new JobStateRecord
        {
            Name = name,
            NextRunAt = nextRunAt,
            LockedUntil = lockedUntil,
            LastStatus = lastStatus,
            LastError = lastError,
        });
        await context.SaveChangesAsync(Ct);
    }

    /// <summary>A second job type that claims <see cref="CountingJob"/>'s name.</summary>
    private sealed class SameNameAsCountingJob : IRecurringJob
    {
        public string Name => CountingJob.JobName;

        public TimeSpan DefaultInterval => TimeSpan.FromHours(1);

        public Task<JobResult> RunAsync(CancellationToken cancellationToken) => Task.FromResult(new JobResult(0));
    }

    /// <summary>A job whose name has a dot, which an environment-variable key for its <c>Jobs:{Name}</c> section cannot carry.</summary>
    private sealed class DottedNameJob : IRecurringJob
    {
        public const string JobName = "Test.Dotted";

        public string Name => JobName;

        public TimeSpan DefaultInterval => TimeSpan.FromHours(1);

        public Task<JobResult> RunAsync(CancellationToken cancellationToken) => Task.FromResult(new JobResult(0));
    }

    /// <summary>Records every measurement of the platform meter, of every host, while it lives: the value and its tags as text.</summary>
    private sealed class PlatformMeasurements : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Lock _lock = new();
        private readonly List<(string Instrument, string Text)> _measurements = [];

        public PlatformMeasurements()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == RecurringJobMetrics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument.Name, value.ToString(CultureInfo.InvariantCulture), tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument.Name, value.ToString("R", CultureInfo.InvariantCulture), tags));
            _listener.Start();
        }

        public IReadOnlyList<string> Of(string instrument)
        {
            lock (_lock)
            {
                return [.. _measurements.Where(measurement => measurement.Instrument == instrument).Select(measurement => measurement.Text)];
            }
        }

        public void Dispose() => _listener.Dispose();

        private void Add(string instrument, string value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var text = string.Join(" ", [value, .. tags.ToArray().OrderBy(tag => tag.Key).Select(tag => $"{tag.Key}={tag.Value}")]);
            lock (_lock)
            {
                _measurements.Add((instrument, text));
            }
        }
    }
}
