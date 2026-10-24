using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Auth.Infrastructure.Retention;

/// <summary>
/// How long the Auth module keeps its time-bound rows before a recurring cleanup job deletes them (section <c>Auth:Retention</c>,
/// ADR 0028). A row goes only when it is strictly older than the retention; <see langword="null"/> or zero turns that cleanup off and a
/// negative value is refused when the host starts. <c>RecoveryCodes</c> and <c>PasswordHistory</c> are not time-based and have no entry.
/// </summary>
internal sealed class AuthRetentionOptions : IValidatableObject
{
    internal const string SectionName = "Auth:Retention";

    /// <summary>How long a verification code is kept after <c>ExpiresAt</c>.</summary>
    public TimeSpan? VerificationCodes { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a refresh token is kept after its own <c>ExpiresAt</c>, whatever its session's state. A token inside its lifetime is never
    /// deleted, so presenting a used one still reports a reuse.
    /// </summary>
    public TimeSpan? RefreshTokens { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How long a session is kept after <c>RevokedAt</c> or <c>ExpiresAt</c>, whichever is first; its refresh tokens go with it.</summary>
    public TimeSpan? Sessions { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How long a two-step sign-in challenge is kept after <c>ExpiresAt</c>.</summary>
    public TimeSpan? MfaChallenges { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How long a trusted device is kept after <c>ExpiresAt</c> or <c>RevokedAt</c>, whichever is first.</summary>
    public TimeSpan? TrustedDevices { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// How long an audit log entry is kept after <c>OccurredAt</c>. Off by default: no archive exists yet to move the entries to, so
    /// deleting them would lose them.
    /// </summary>
    public TimeSpan? AuditLogs { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var (name, value) in new (string Name, TimeSpan? Value)[]
        {
            (nameof(VerificationCodes), VerificationCodes),
            (nameof(RefreshTokens), RefreshTokens),
            (nameof(Sessions), Sessions),
            (nameof(MfaChallenges), MfaChallenges),
            (nameof(TrustedDevices), TrustedDevices),
            (nameof(AuditLogs), AuditLogs),
        })
        {
            if (value < TimeSpan.Zero)
            {
                yield return new ValidationResult($"{name} may not be negative; use zero to turn the cleanup off.", [name]);
            }
        }
    }
}
