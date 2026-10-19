using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Auth.Application.Mfa;

/// <summary>
/// Two-step verification (section <c>Auth:Mfa</c>): the name authenticator apps show, how long and how often a sign-in challenge may be
/// answered, and whether and for how long a device can be remembered.
/// </summary>
internal sealed class MfaOptions : IValidatableObject
{
    internal const string SectionName = "Auth:Mfa";

    /// <summary>The longest <see cref="Issuer"/>; authenticator apps show it next to the account name.</summary>
    internal const int MaxIssuerLength = 64;

    /// <summary>The issuer of the <c>otpauth://</c> URI, the name the authenticator app files the account under. No <c>:</c>.</summary>
    [Required]
    [StringLength(MaxIssuerLength, MinimumLength = 1)]
    public string Issuer { get; set; } = "TemplateName";

    /// <summary>How long the challenge a correct password starts can be answered with a code.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "00:15:00")]
    public TimeSpan ChallengeLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The wrong codes one challenge accepts; the one that reaches it ends the challenge (each also counts toward the lockout).</summary>
    [Range(1, 10)]
    public int MaxChallengeAttempts { get; set; } = 5;

    /// <summary>Whether a user may remember a device after a second factor, so the next sign-ins from it need the password only.</summary>
    public bool TrustedDevicesEnabled { get; set; } = true;

    /// <summary>How long a remembered device skips the second factor, counted from when it was remembered (absolute, never extended).</summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "365.00:00:00")]
    public TimeSpan TrustedDeviceLifetime { get; set; } = TimeSpan.FromDays(30);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // The label of the otpauth URI is "issuer:account"; a colon in the issuer would read as a second separator.
        if (Issuer is not null && Issuer.Contains(':', StringComparison.Ordinal))
        {
            yield return new ValidationResult($"{nameof(Issuer)} may not contain ':'.", [nameof(Issuer)]);
        }
    }
}
