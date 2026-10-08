using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Infrastructure.Observability;

namespace TemplateName.UnitTests.Auth;

public sealed class AuthMetricsTests : IDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, long Value, KeyValuePair<string, object?>[] Tags)> _measurements = [];
    private readonly AuthMetrics _sut;

    public AuthMetricsTests()
    {
        _sut = new AuthMetrics(_services.GetRequiredService<IMeterFactory>());
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == AuthMetrics.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => _measurements.Add((instrument.Name, value, tags.ToArray())));
        _listener.Start();
    }

    [Theory]
    [InlineData(nameof(LoginOutcome.Succeeded), "succeeded")]
    [InlineData(nameof(LoginOutcome.InvalidCredentials), "invalid_credentials")]
    [InlineData(nameof(LoginOutcome.Locked), "locked")]
    [InlineData(nameof(LoginOutcome.Unverified), "unverified")]
    [InlineData(nameof(LoginOutcome.Inactive), "inactive")]
    public void Login_counts_one_with_only_the_result_tag(string outcome, string result)
    {
        _sut.RecordLogin(Enum.Parse<LoginOutcome>(outcome));

        var measurement = _measurements.ShouldHaveSingleItem();
        measurement.Instrument.ShouldBe("auth.logins");
        measurement.Value.ShouldBe(1);
        measurement.Tags.ShouldBe([new KeyValuePair<string, object?>("result", result)]);
    }

    [Fact]
    public void Registration_and_token_reuse_count_one_without_tags()
    {
        _sut.RecordRegistration();
        _sut.RecordTokenReuse();

        _measurements.Select(measurement => measurement.Instrument).ShouldBe(["auth.registrations", "auth.token_reuse_detected"]);
        _measurements.ShouldAllBe(measurement => measurement.Value == 1 && measurement.Tags.Length == 0);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _services.Dispose();
    }
}
