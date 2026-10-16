using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Observability;

namespace TemplateName.UnitTests.Notifications;

public sealed class NotificationsMetricsTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> _measurements = [];
    private readonly List<(string Instrument, string? Unit, double Value, KeyValuePair<string, object?>[] Tags)> _durations = [];
    private readonly NotificationsMetrics _sut;

    public NotificationsMetricsTests()
    {
        _sut = new NotificationsMetrics(_services.GetRequiredService<IMeterFactory>());
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == NotificationsMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => _measurements.Add((instrument.Name, value, tags.ToArray())));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => _durations.Add((instrument.Name, instrument.Unit, value, tags.ToArray())));
        _listener.Start();
    }

    [Fact]
    public void Meter_is_named_after_the_module() => NotificationsMetrics.MeterName.ShouldBe("TemplateName.Notifications");

    [Fact]
    public void Created_counts_one_with_only_the_type_tag()
    {
        _sut.RecordCreated("auth.password_changed");

        var measurement = _measurements.ShouldHaveSingleItem();
        measurement.Instrument.ShouldBe("notifications.created");
        measurement.Value.ShouldBe(1);
        measurement.Tags.ShouldBe([new KeyValuePair<string, object?>("type", "auth.password_changed")]);
    }

    // The enums are internal, so the cases name them; the method stays public for the test runner.
    [Theory]
    [InlineData(nameof(NotificationChannel.Email), nameof(DeliveryResult.Sent), "email", "sent")]
    [InlineData(nameof(NotificationChannel.InApp), nameof(DeliveryResult.Retry), "in_app", "retry")]
    [InlineData(nameof(NotificationChannel.Email), nameof(DeliveryResult.DeadLettered), "email", "dead_lettered")]
    [InlineData(nameof(NotificationChannel.InApp), nameof(DeliveryResult.Expired), "in_app", "expired")]
    public void Delivery_counts_the_result_and_records_the_duration_by_channel(string channel, string result, string channelTag, string resultTag)
    {
        _sut.RecordDelivery(Enum.Parse<NotificationChannel>(channel), Enum.Parse<DeliveryResult>(result), TimeSpan.FromMilliseconds(12.5));

        var counted = _measurements.ShouldHaveSingleItem();
        counted.Instrument.ShouldBe("notifications.deliveries");
        counted.Value.ShouldBe(1);
        counted.Tags.ShouldBe([new("channel", channelTag), new("result", resultTag)], ignoreOrder: true);

        var timed = _durations.ShouldHaveSingleItem();
        timed.Instrument.ShouldBe("notifications.delivery.duration");
        timed.Unit.ShouldBe("ms");
        timed.Value.ShouldBe(12.5);
        timed.Tags.ShouldBe([new KeyValuePair<string, object?>("channel", channelTag)]);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _services.Dispose();
    }
}
