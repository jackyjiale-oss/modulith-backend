using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Auth.Application.Authentication;

/// <summary>
/// The lockout after wrong passwords (section <c>Auth:Lockout</c>): after <see cref="MaxFailedAttempts"/> failures in a row the account is
/// locked for a fixed <see cref="Duration"/> (decision D4: not progressive). A locked account answers like a wrong password (D10).
/// </summary>
internal sealed class LockoutOptions
{
    internal const string SectionName = "Auth:Lockout";

    /// <summary>The failure that reaches this count locks the account.</summary>
    [Range(1, 100)]
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>How long the account stays locked; it opens again exactly at the end.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(15);
}
