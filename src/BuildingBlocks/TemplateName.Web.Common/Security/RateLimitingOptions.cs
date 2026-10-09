using System.ComponentModel.DataAnnotations;

namespace TemplateName.Web.Common.Security;

/// <summary>
/// Rate limits (section <c>RateLimiting</c>): the global fixed window per authenticated user, or per client address when anonymous, and
/// the <see cref="RateLimitPolicies.AuthStrict"/> policy, a fixed window per client address for anonymous credential endpoints.
/// </summary>
internal sealed class RateLimitingOptions
{
    internal const string SectionName = "RateLimiting";

    /// <summary>Requests allowed per partition in each <see cref="GlobalWindow"/>.</summary>
    [Range(1, int.MaxValue)]
    public int GlobalPermitLimit { get; set; } = 300;

    /// <summary>Length of the fixed window.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan GlobalWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Requests allowed per client address in each <see cref="AuthStrictWindow"/> on an endpoint with the <c>auth-strict</c> policy.</summary>
    [Range(1, int.MaxValue)]
    public int AuthStrictPermitLimit { get; set; } = 10;

    /// <summary>Length of the <c>auth-strict</c> fixed window.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan AuthStrictWindow { get; set; } = TimeSpan.FromMinutes(1);
}
