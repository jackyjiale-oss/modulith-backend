using System.ComponentModel.DataAnnotations;

namespace TemplateName.Web.Common.Security;

/// <summary>Global rate limit (section <c>RateLimiting</c>): a fixed window per authenticated user, or per client address when anonymous.</summary>
internal sealed class RateLimitingOptions
{
    internal const string SectionName = "RateLimiting";

    /// <summary>Requests allowed per partition in each <see cref="GlobalWindow"/>.</summary>
    [Range(1, int.MaxValue)]
    public int GlobalPermitLimit { get; set; } = 300;

    /// <summary>Length of the fixed window.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan GlobalWindow { get; set; } = TimeSpan.FromMinutes(1);
}
