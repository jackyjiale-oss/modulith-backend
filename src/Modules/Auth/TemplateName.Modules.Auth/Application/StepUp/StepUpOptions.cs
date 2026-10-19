using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Auth.Application.StepUp;

/// <summary>
/// Step-up authentication (section <c>Auth:StepUp</c>): how long after the user last proved their identity (the access token's
/// <c>auth_time</c>) a protected action is still allowed without proving it again.
/// </summary>
internal sealed class StepUpOptions
{
    internal const string SectionName = "Auth:StepUp";

    /// <summary>A step-up satisfies <see cref="StepUpPolicy"/> while <c>now - auth_time</c> is less than this.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(10);
}
