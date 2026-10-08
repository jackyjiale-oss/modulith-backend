using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Auth.Application.Verification;

/// <summary>
/// The emailed single-use links (section <c>Auth:Verification</c>): how long each kind stays valid and how often a confirmation may be
/// sent again. The lifetimes stay inside the spec's 15 to 60 minutes.
/// </summary>
internal sealed class VerificationOptions
{
    internal const string SectionName = "Auth:Verification";

    /// <summary>How long an email-confirmation link works.</summary>
    [Range(typeof(TimeSpan), "00:15:00", "01:00:00")]
    public TimeSpan EmailLifetime { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How long a password-reset link works.</summary>
    [Range(typeof(TimeSpan), "00:15:00", "01:00:00")]
    public TimeSpan PasswordResetLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The least time between two confirmation emails to one account, counted from the last code issued; a resend inside it sends
    /// nothing. Zero switches the cooldown off.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:00", "01:00:00")]
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromMinutes(1);
}
