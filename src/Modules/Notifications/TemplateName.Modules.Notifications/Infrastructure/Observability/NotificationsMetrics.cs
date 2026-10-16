using System.Diagnostics.Metrics;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Infrastructure.Observability;

/// <summary>
/// The module's instruments on the meter <see cref="MeterName"/>, created through <see cref="IMeterFactory"/>. <c>AddNotificationsModule</c>
/// adds the meter to OpenTelemetry, so the counters are exported whenever the host exports metrics. Tags carry only fixed values.
/// </summary>
internal sealed class NotificationsMetrics : INotificationsMetrics
{
    internal const string MeterName = "TemplateName.Notifications";

    private const string TypeTag = "type";
    private const string ChannelTag = "channel";
    private const string ResultTag = "result";

    private readonly Counter<long> _created;
    private readonly Counter<long> _deliveries;
    private readonly Histogram<double> _deliveryDuration;

    public NotificationsMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _created = meter.CreateCounter<long>("notifications.created", unit: "{notification}", description: "Notifications created from consumed events, by type.");
        _deliveries = meter.CreateCounter<long>("notifications.deliveries", unit: "{delivery}", description: "Delivery attempts the worker settled, by channel and result.");
        _deliveryDuration = meter.CreateHistogram<double>("notifications.delivery.duration", unit: "ms", description: "How long one delivery attempt took, from loading it to saving its outcome, by channel.");
    }

    public void RecordCreated(string typeCode) => _created.Add(1, new KeyValuePair<string, object?>(TypeTag, typeCode));

    public void RecordDelivery(NotificationChannel channel, DeliveryResult result, TimeSpan duration)
    {
        var channelTag = new KeyValuePair<string, object?>(ChannelTag, ChannelName(channel));
        _deliveries.Add(1, channelTag, new KeyValuePair<string, object?>(ResultTag, ResultName(result)));
        _deliveryDuration.Record(duration.TotalMilliseconds, channelTag);
    }

    private static string ChannelName(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => "email",
        NotificationChannel.InApp => "in_app",
        _ => "unknown",
    };

    private static string ResultName(DeliveryResult result) => result switch
    {
        DeliveryResult.Sent => "sent",
        DeliveryResult.Retry => "retry",
        DeliveryResult.DeadLettered => "dead_lettered",
        DeliveryResult.Expired => "expired",
        _ => "unknown",
    };
}
