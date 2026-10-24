using System.Diagnostics.Metrics;

namespace TemplateName.Infrastructure.Common.Jobs;

/// <summary>
/// The scheduler's instruments on the meter <see cref="MeterName"/>, created through <see cref="IMeterFactory"/>; the host's
/// OpenTelemetry setup (<c>ObservabilityExtensions</c>) exports the meter. Tags carry only the job name and a fixed outcome.
/// </summary>
internal sealed class RecurringJobMetrics
{
    internal const string MeterName = "TemplateName.Platform";

    private const string JobTag = "job";
    private const string OutcomeTag = "outcome";

    private readonly Counter<long> _runs;
    private readonly Histogram<double> _duration;
    private readonly Counter<long> _processed;

    public RecurringJobMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _runs = meter.CreateCounter<long>("platform.jobs.runs", unit: "{run}", description: "Recurring-job runs, by job and outcome.");
        _duration = meter.CreateHistogram<double>("platform.jobs.duration", unit: "s", description: "How long recurring-job runs took, by job.");
        _processed = meter.CreateCounter<long>(
            "platform.jobs.processed",
            unit: "{item}",
            description: "Rows or items that successful recurring-job runs handled, by job.");
    }

    public void RecordRun(string jobName, bool succeeded, TimeSpan duration, int processed)
    {
        var job = new KeyValuePair<string, object?>(JobTag, jobName);
        _runs.Add(1, job, new KeyValuePair<string, object?>(OutcomeTag, succeeded ? "succeeded" : "failed"));
        _duration.Record(duration.TotalSeconds, job);
        if (succeeded)
        {
            _processed.Add(processed, job);
        }
    }
}
