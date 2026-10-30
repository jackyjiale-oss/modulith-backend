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
    private const string ProviderTag = "provider";
    private const string OutcomeTag = "outcome";

    private readonly Counter<long> _logins;
    private readonly Counter<long> _registrations;
    private readonly Counter<long> _tokenReuses;
    private readonly Counter<long> _stepUps;
    private readonly Counter<long> _mfaVerifications;
    private readonly Counter<long> _externalLogins;

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
        _externalLogins = meter.CreateCounter<long>(
            "auth.external_logins",
            unit: "{attempt}",
            description: "External sign-in attempts that reached the handler, by configured provider and outcome.");
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

    // The provider tag comes only from a configured provider (the catalog's name), never from the route, so its values are bounded.
    public void RecordExternalLogin(ExternalProvider? provider, ExternalLoginOutcome outcome)
    {
        var outcomeTag = new KeyValuePair<string, object?>(OutcomeTag, OutcomeOf(outcome));
        if (provider is null)
        {
            _externalLogins.Add(1, outcomeTag);
            return;
        }

        _externalLogins.Add(1, new KeyValuePair<string, object?>(ProviderTag, provider.Name), outcomeTag);
    }

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

    private static string OutcomeOf(ExternalLoginOutcome outcome) => outcome switch
    {
        ExternalLoginOutcome.Succeeded => "succeeded",
        ExternalLoginOutcome.MfaRequired => "mfa_required",
        ExternalLoginOutcome.ProviderNotFound => "provider_not_found",
        ExternalLoginOutcome.InvalidToken => "invalid_token",
        ExternalLoginOutcome.ProviderUnavailable => "provider_unavailable",
        ExternalLoginOutcome.EmailUnverified => "email_unverified",
        ExternalLoginOutcome.LinkRequired => "link_required",
        ExternalLoginOutcome.SignUpDisabled => "sign_up_disabled",
        ExternalLoginOutcome.Inactive => "inactive",
        ExternalLoginOutcome.Locked => "locked",
        ExternalLoginOutcome.InvalidCredentials => "invalid_credentials",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown external login outcome."),
    };

    private static string FactorOf(MfaFactor? factor) => factor switch
    {
        null => "none",
        MfaFactor.Totp => "totp",
        MfaFactor.RecoveryCode => "recovery_code",
        _ => throw new ArgumentOutOfRangeException(nameof(factor), factor, "Unknown factor."),
    };
}
