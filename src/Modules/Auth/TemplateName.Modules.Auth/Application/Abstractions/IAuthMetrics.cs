using TemplateName.Modules.Auth.Application.Mfa;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>
/// The module's counters (meter <c>TemplateName.Auth</c>). The only tag is a result, a fixed value: never an email address, a user id or
/// anything else a caller typed.
/// </summary>
internal interface IAuthMetrics
{
    /// <summary>Counts one login attempt that reached the handler (<c>auth.logins</c>, tag <c>result</c>).</summary>
    void RecordLogin(LoginOutcome outcome);

    /// <summary>
    /// Counts one accepted registration request (<c>auth.registrations</c>), for a new and an existing address alike: the metric must
    /// not tell them apart any more than the response does.
    /// </summary>
    void RecordRegistration();

    /// <summary>Counts one refresh token presented again after it was used or revoked (<c>auth.token_reuse_detected</c>).</summary>
    void RecordTokenReuse();

    /// <summary>
    /// Counts one step-up that reached a decision (<c>auth.step_ups</c>, tag <c>result</c> = <c>succeeded</c> or <c>failed</c>); a
    /// failure is any 400 of the handler (a wrong proof, a factor the account may not use, a locked account), never a 401.
    /// </summary>
    void RecordStepUp(bool succeeded);

    /// <summary>
    /// Counts one verify of a sign-in challenge that reached a decision (<c>auth.mfa_verifications</c>, tags <c>result</c> =
    /// <c>succeeded</c>, <c>invalid_code</c> or <c>invalid_challenge</c>, and <c>factor</c> = <c>totp</c>, <c>recovery_code</c> or
    /// <c>none</c>). <paramref name="factor"/> is the factor accepted or tried, null when the challenge was refused before a factor
    /// counted. A 400 of the validator and a 429 are not counted.
    /// </summary>
    void RecordMfaVerification(MfaVerificationOutcome outcome, MfaFactor? factor);
}
