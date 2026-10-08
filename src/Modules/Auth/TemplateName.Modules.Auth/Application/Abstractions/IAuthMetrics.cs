namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>
/// The module's counters (meter <c>TemplateName.Auth</c>). The only tag is the login result, a fixed value: never an email address, a
/// user id or anything else a caller typed.
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
}
