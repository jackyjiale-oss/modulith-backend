using System.Diagnostics.Metrics;
using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.Modules.Notifications.Infrastructure.Observability;

/// <summary>
/// The module's counters on the meter <see cref="MeterName"/>, created through <see cref="IMeterFactory"/>. <c>AddNotificationsModule</c>
/// adds the meter to OpenTelemetry, so the counters are exported whenever the host exports metrics. Tags carry only fixed values.
/// </summary>
internal sealed class NotificationsMetrics : INotificationsMetrics
{
    internal const string MeterName = "TemplateName.Notifications";

    private const string TypeTag = "type";

    private readonly Counter<long> _created;

    public NotificationsMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _created = meter.CreateCounter<long>("notifications.created", unit: "{notification}", description: "Notifications created from consumed events, by type.");
    }

    public void RecordCreated(string typeCode) => _created.Add(1, new KeyValuePair<string, object?>(TypeTag, typeCode));
}
