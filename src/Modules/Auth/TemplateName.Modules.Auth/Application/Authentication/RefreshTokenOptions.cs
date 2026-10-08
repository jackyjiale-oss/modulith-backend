using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Auth.Application.Authentication;

/// <summary>
/// How long a sign-in lasts (section <c>Auth:RefreshToken</c>): each refresh token works for <see cref="SlidingLifetime"/> from its issue,
/// and no token outlives its session's <see cref="AbsoluteLifetime"/>, counted from the login.
/// </summary>
internal sealed class RefreshTokenOptions : IValidatableObject
{
    internal const string SectionName = "Auth:RefreshToken";

    /// <summary>How long one refresh token works; a refresh issues the next one, so an active client stays signed in.</summary>
    [Range(typeof(TimeSpan), "00:15:00", "365.00:00:00")]
    public TimeSpan SlidingLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>How long a session can last at most, however often it is refreshed; then the user signs in again.</summary>
    [Range(typeof(TimeSpan), "00:15:00", "365.00:00:00")]
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromDays(90);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SlidingLifetime > AbsoluteLifetime)
        {
            yield return new ValidationResult(
                $"{nameof(SlidingLifetime)} may not be greater than {nameof(AbsoluteLifetime)}.",
                [nameof(SlidingLifetime)]);
        }
    }
}
