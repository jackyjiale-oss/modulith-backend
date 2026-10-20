using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using TemplateName.Infrastructure.Common.Jobs;

namespace TemplateName.Infrastructure.Common.Outbox;

/// <summary>
/// The gauge <c>platform.outbox.abandoned{context}</c> on the platform meter: how many outbox messages a context has given up on
/// (<c>ProcessedAt IS NULL</c> and <c>AttemptCount &gt;= MaxAttempts</c>). The cleanup job sets it after each run; the gauge reports the
/// last value until the next run, so it stays visible between daily runs.
/// </summary>
internal sealed class OutboxAbandonedGauge
{
    internal const string InstrumentName = "platform.outbox.abandoned";

    private readonly ConcurrentDictionary<string, long> _abandonedByContext = new(StringComparer.Ordinal);

    public OutboxAbandonedGauge(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(RecurringJobMetrics.MeterName);
        meter.CreateObservableGauge(
            InstrumentName,
            ObserveAbandoned,
            unit: "{message}",
            description: "Outbox messages that ran out of attempts and wait for an operator, by context.");
    }

    /// <summary>Sets the abandoned count of <paramref name="context"/>, the context's name without <c>DbContext</c>.</summary>
    public void Set(string context, long abandoned) => _abandonedByContext[context] = abandoned;

    private IEnumerable<Measurement<long>> ObserveAbandoned()
        => _abandonedByContext.Select(pair => new Measurement<long>(pair.Value, new KeyValuePair<string, object?>("context", pair.Key)));
}
