using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Modules.Notifications.Infrastructure.Observability;

namespace TemplateName.UnitTests.Notifications;

public sealed class NotificationsMetricsTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> _measurements = [];
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

    public void Dispose()
    {
        _listener.Dispose();
        _services.Dispose();
    }
}
