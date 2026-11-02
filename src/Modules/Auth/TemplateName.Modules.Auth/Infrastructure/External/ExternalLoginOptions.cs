using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Auth.Infrastructure.External;

/// <summary>
/// Social login settings (section <c>Auth:External</c>). The range of <see cref="MaxTokenAge"/> is checked by data annotations and every
/// enabled provider by <see cref="ExternalLoginOptionsValidator"/>, both at start-up.
/// </summary>
internal sealed class ExternalLoginOptions
{
    internal const string SectionName = "Auth:External";

    /// <summary>The configuration path of <see cref="Providers"/>, used in every message about an entry.</summary>
    internal const string ProvidersPath = SectionName + ":" + nameof(Providers);

    /// <summary>Whether a first social sign-in with no matching account creates one. Turn it off for an invite-only application.</summary>
    public bool AllowSignUp { get; set; } = true;

    /// <summary>How old an ID token's <c>iat</c> may be, which bounds how long a stolen token and its nonce can be replayed.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan MaxTokenAge { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The providers by configuration name, compared without regard to case. Every one is off until its <c>Enabled</c> is set.</summary>
    public Dictionary<string, ExternalProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
