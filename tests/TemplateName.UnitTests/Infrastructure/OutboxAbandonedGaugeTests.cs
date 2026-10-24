using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TemplateName.Infrastructure.Common.Jobs;
using TemplateName.Infrastructure.Common.Outbox;

namespace TemplateName.UnitTests.Infrastructure;

public sealed class OutboxAbandonedGaugeTests : IDisposable
{
    private const string DailyJobName = "AuthOutboxCleanup";
    private const string RescheduledJobName = "NotificationsOutboxCleanup";

    // The window a value is reported for: the job's interval, plus the longest a run may take (the lease) and one tick of the scheduler.
    private static readonly TimeSpan Margin = new JobsOptions().LeaseDuration + new JobsOptions().TickInterval;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 3, 0, 0, TimeSpan.Zero));
    private readonly ServiceProvider _services;
    private readonly IMeterFactory _meterFactory;
    private readonly OutboxAbandonedGauge _sut;
    private readonly StubJob _dailyJob = new(DailyJobName, TimeSpan.FromDays(1));

    public OutboxAbandonedGaugeTests()
    {
        _services = new ServiceCollection()
            .AddMetrics()
            .Configure<RecurringJobOptions>(RescheduledJobName, options => options.Interval = TimeSpan.FromHours(1))
            .BuildServiceProvider();
        _meterFactory = _services.GetRequiredService<IMeterFactory>();
        _sut = new OutboxAbandonedGauge(
            _meterFactory,
            _time,
            Options.Create(new JobsOptions()),
            _services.GetRequiredService<IOptionsMonitor<RecurringJobOptions>>());
    }

    public void Dispose() => _services.Dispose();

    [Fact]
    public void A_value_is_reported_until_the_job_interval_and_the_margin_have_passed()
    {
        _sut.Set("Auth", 3, _dailyJob);

        _time.Advance(TimeSpan.FromDays(1) + Margin);

        Observe().ShouldBe(new Dictionary<string, long> { ["Auth"] = 3 });
    }

    [Fact]
    public void A_value_older_than_the_job_interval_and_the_margin_is_not_reported()
    {
        _sut.Set("Auth", 3, _dailyJob);

        _time.Advance(TimeSpan.FromDays(1) + Margin + TimeSpan.FromSeconds(1));

        Observe().ShouldBeEmpty();
    }

    [Fact]
    public void A_new_value_replaces_the_old_one_and_starts_its_window_again()
    {
        _sut.Set("Auth", 3, _dailyJob);
        _time.Advance(TimeSpan.FromDays(1));

        _sut.Set("Auth", 5, _dailyJob);
        _time.Advance(TimeSpan.FromDays(1) + Margin);

        Observe().ShouldBe(new Dictionary<string, long> { ["Auth"] = 5 });
    }

    [Fact]
    public void The_configured_interval_of_the_job_sets_the_window_not_its_default()
    {
        _sut.Set("Notifications", 2, new StubJob(RescheduledJobName, TimeSpan.FromDays(1)));

        _time.Advance(TimeSpan.FromHours(1) + Margin);
        Observe().ShouldBe(new Dictionary<string, long> { ["Notifications"] = 2 });

        _time.Advance(TimeSpan.FromSeconds(1));
        Observe().ShouldBeEmpty();
    }

    [Fact]
    public void Each_context_expires_on_its_own()
    {
        _sut.Set("Auth", 3, _dailyJob);
        _time.Advance(TimeSpan.FromHours(12));
        _sut.Set("Notifications", 0, _dailyJob);

        _time.Advance(TimeSpan.FromHours(12) + Margin + TimeSpan.FromSeconds(1));

        Observe().ShouldBe(new Dictionary<string, long> { ["Notifications"] = 0 });
    }

    private Dictionary<string, long> Observe()
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            // Only this test's meter: other tests create meters of the same name.
            if (ReferenceEquals(instrument.Meter.Scope, _meterFactory) && instrument.Name == OutboxAbandonedGauge.InstrumentName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "context")
                {
                    values[(string)tag.Value!] = value;
                }
            }
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return values;
    }

    private sealed class StubJob(string name, TimeSpan defaultInterval) : IRecurringJob
    {
        public string Name => name;

        public TimeSpan DefaultInterval => defaultInterval;

        public Task<JobResult> RunAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
