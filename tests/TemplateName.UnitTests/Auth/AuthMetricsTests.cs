using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;
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
    [InlineData(nameof(LoginOutcome.MfaRequired), "mfa_required")]
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

    [Theory]
    [InlineData(true, "succeeded")]
    [InlineData(false, "failed")]
    public void Step_up_counts_one_with_only_the_result_tag(bool succeeded, string result)
    {
        _sut.RecordStepUp(succeeded);

        var measurement = _measurements.ShouldHaveSingleItem();
        measurement.Instrument.ShouldBe("auth.step_ups");
        measurement.Value.ShouldBe(1);
        measurement.Tags.ShouldBe([new KeyValuePair<string, object?>("result", result)]);
    }

    [Theory]
    [InlineData(nameof(MfaVerificationOutcome.Succeeded), nameof(MfaFactor.Totp), "succeeded", "totp")]
    [InlineData(nameof(MfaVerificationOutcome.Succeeded), nameof(MfaFactor.RecoveryCode), "succeeded", "recovery_code")]
    [InlineData(nameof(MfaVerificationOutcome.InvalidCode), nameof(MfaFactor.Totp), "invalid_code", "totp")]
    [InlineData(nameof(MfaVerificationOutcome.InvalidCode), nameof(MfaFactor.RecoveryCode), "invalid_code", "recovery_code")]
    [InlineData(nameof(MfaVerificationOutcome.InvalidChallenge), null, "invalid_challenge", "none")]
    public void Mfa_verification_counts_one_with_the_result_and_factor_tags(string outcome, string? factor, string result, string factorTag)
    {
        _sut.RecordMfaVerification(Enum.Parse<MfaVerificationOutcome>(outcome), factor is null ? null : Enum.Parse<MfaFactor>(factor));

        var measurement = _measurements.ShouldHaveSingleItem();
        measurement.Instrument.ShouldBe("auth.mfa_verifications");
        measurement.Value.ShouldBe(1);
        measurement.Tags.ShouldBe([new KeyValuePair<string, object?>("result", result), new KeyValuePair<string, object?>("factor", factorTag)]);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _services.Dispose();
    }
}
