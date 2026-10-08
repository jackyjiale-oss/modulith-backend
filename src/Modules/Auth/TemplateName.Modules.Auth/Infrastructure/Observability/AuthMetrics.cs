using System.Diagnostics.Metrics;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Infrastructure.Observability;

/// <summary>
/// The module's counters on the meter <see cref="MeterName"/>, created through <see cref="IMeterFactory"/>. <c>AddAuthModule</c> adds the
/// meter to OpenTelemetry, so the counters are exported whenever the host exports metrics. Tags carry only fixed values.
/// </summary>
internal sealed class AuthMetrics : IAuthMetrics
{
    internal const string MeterName = "TemplateName.Auth";

    private const string ResultTag = "result";

    private readonly Counter<long> _logins;
    private readonly Counter<long> _registrations;
    private readonly Counter<long> _tokenReuses;

    public AuthMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _logins = meter.CreateCounter<long>("auth.logins", unit: "{attempt}", description: "Login attempts, by result.");
        _registrations = meter.CreateCounter<long>("auth.registrations", unit: "{request}", description: "Accepted registration requests, new and existing addresses alike.");
        _tokenReuses = meter.CreateCounter<long>("auth.token_reuse_detected", unit: "{token}", description: "Refresh tokens presented again after use; each revokes its session.");
    }

    public void RecordLogin(LoginOutcome outcome) => _logins.Add(1, new KeyValuePair<string, object?>(ResultTag, ResultOf(outcome)));

    public void RecordRegistration() => _registrations.Add(1);

    public void RecordTokenReuse() => _tokenReuses.Add(1);

    private static string ResultOf(LoginOutcome outcome) => outcome switch
    {
        LoginOutcome.Succeeded => "succeeded",
        LoginOutcome.InvalidCredentials => "invalid_credentials",
        LoginOutcome.Locked => "locked",
        LoginOutcome.Unverified => "unverified",
        LoginOutcome.Inactive => "inactive",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown login outcome."),
    };
}
