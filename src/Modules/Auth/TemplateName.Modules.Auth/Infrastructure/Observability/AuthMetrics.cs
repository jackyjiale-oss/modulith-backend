using System.Diagnostics.Metrics;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Application.Mfa;

namespace TemplateName.Modules.Auth.Infrastructure.Observability;

/// <summary>
/// The module's counters on the meter <see cref="MeterName"/>, created through <see cref="IMeterFactory"/>. <c>AddAuthModule</c> adds the
/// meter to OpenTelemetry, so the counters are exported whenever the host exports metrics. Tags carry only fixed values.
/// </summary>
internal sealed class AuthMetrics : IAuthMetrics
{
    internal const string MeterName = "TemplateName.Auth";

    private const string ResultTag = "result";
    private const string FactorTag = "factor";

    private readonly Counter<long> _logins;
    private readonly Counter<long> _registrations;
    private readonly Counter<long> _tokenReuses;
    private readonly Counter<long> _stepUps;
    private readonly Counter<long> _mfaVerifications;

    public AuthMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _logins = meter.CreateCounter<long>("auth.logins", unit: "{attempt}", description: "Login attempts, by result.");
        _registrations = meter.CreateCounter<long>("auth.registrations", unit: "{request}", description: "Accepted registration requests, new and existing addresses alike.");
        _tokenReuses = meter.CreateCounter<long>("auth.token_reuse_detected", unit: "{token}", description: "Refresh tokens presented again after use; each revokes its session.");
        _stepUps = meter.CreateCounter<long>("auth.step_ups", unit: "{attempt}", description: "Step-up attempts that reached a decision, by result.");
        _mfaVerifications = meter.CreateCounter<long>(
            "auth.mfa_verifications",
            unit: "{attempt}",
            description: "Verifications of a sign-in challenge that reached a decision, by result and factor.");
    }

    public void RecordLogin(LoginOutcome outcome) => _logins.Add(1, new KeyValuePair<string, object?>(ResultTag, ResultOf(outcome)));

    public void RecordRegistration() => _registrations.Add(1);

    public void RecordTokenReuse() => _tokenReuses.Add(1);

    public void RecordStepUp(bool succeeded) =>
        _stepUps.Add(1, new KeyValuePair<string, object?>(ResultTag, succeeded ? "succeeded" : "failed"));

    public void RecordMfaVerification(MfaVerificationOutcome outcome, MfaFactor? factor) =>
        _mfaVerifications.Add(
            1,
            new KeyValuePair<string, object?>(ResultTag, ResultOf(outcome)),
            new KeyValuePair<string, object?>(FactorTag, FactorOf(factor)));

    private static string ResultOf(LoginOutcome outcome) => outcome switch
    {
        LoginOutcome.Succeeded => "succeeded",
        LoginOutcome.InvalidCredentials => "invalid_credentials",
        LoginOutcome.Locked => "locked",
        LoginOutcome.Unverified => "unverified",
        LoginOutcome.Inactive => "inactive",
        LoginOutcome.MfaRequired => "mfa_required",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown login outcome."),
    };

    private static string ResultOf(MfaVerificationOutcome outcome) => outcome switch
    {
        MfaVerificationOutcome.Succeeded => "succeeded",
        MfaVerificationOutcome.InvalidCode => "invalid_code",
        MfaVerificationOutcome.InvalidChallenge => "invalid_challenge",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown verification outcome."),
    };

    private static string FactorOf(MfaFactor? factor) => factor switch
    {
        null => "none",
        MfaFactor.Totp => "totp",
        MfaFactor.RecoveryCode => "recovery_code",
        _ => throw new ArgumentOutOfRangeException(nameof(factor), factor, "Unknown factor."),
    };
}
