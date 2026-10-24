using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Options;
using TemplateName.Infrastructure.Common.Jobs;

namespace TemplateName.Infrastructure.Common.Outbox;

/// <summary>
/// The gauge <c>platform.outbox.abandoned{context}</c> on the platform meter: how many outbox messages a context has given up on
/// (<c>ProcessedAt IS NULL</c> and <c>AttemptCount &gt;= MaxAttempts</c>). The cleanup job sets it after each run, on the instance that ran
/// it; the gauge reports the last value until the job's next run should have replaced it (the job's interval, <c>Jobs:{Name}:Interval</c>
/// or its default, plus <c>Jobs:LeaseDuration</c> and <c>Jobs:TickInterval</c>), then nothing. So an instance that did not run the job
/// lately, or whose last run threw before it counted, reports nothing rather than a stale count; across instances take the maximum of the
/// latest reports, never the sum.
/// </summary>
internal sealed class OutboxAbandonedGauge
{
    internal const string InstrumentName = "platform.outbox.abandoned";

    private readonly ConcurrentDictionary<string, Reading> _readingsByContext = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly IOptions<JobsOptions> _jobs;
    private readonly IOptionsMonitor<RecurringJobOptions> _jobOptions;

    public OutboxAbandonedGauge(
        IMeterFactory meterFactory,
        TimeProvider timeProvider,
        IOptions<JobsOptions> jobs,
        IOptionsMonitor<RecurringJobOptions> jobOptions)
    {
        _timeProvider = timeProvider;
        _jobs = jobs;
        _jobOptions = jobOptions;

        var meter = meterFactory.Create(RecurringJobMetrics.MeterName);
        meter.CreateObservableGauge(
            InstrumentName,
            ObserveAbandoned,
            unit: "{message}",
            description: "Outbox messages that ran out of attempts and wait for an operator, by context.");
    }

    /// <summary>
    /// Sets the abandoned count of <paramref name="context"/>, the context's name without <c>DbContext</c>, as counted now by
    /// <paramref name="job"/>, whose interval bounds how long the value is reported.
    /// </summary>
    public void Set(string context, long abandoned, IRecurringJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var jobs = _jobs.Value;
        var interval = _jobOptions.Get(job.Name).IntervalOr(job.DefaultInterval);
        _readingsByContext[context] = new Reading(abandoned, _timeProvider.GetUtcNow() + interval + jobs.LeaseDuration + jobs.TickInterval);
    }

    private IEnumerable<Measurement<long>> ObserveAbandoned()
    {
        var now = _timeProvider.GetUtcNow();
        return _readingsByContext
            .Where(pair => now <= pair.Value.ReportedUntil)
            .Select(pair => new Measurement<long>(pair.Value.Abandoned, new KeyValuePair<string, object?>("context", pair.Key)))
            .ToList();
    }

    private sealed record Reading(long Abandoned, DateTimeOffset ReportedUntil);
}
