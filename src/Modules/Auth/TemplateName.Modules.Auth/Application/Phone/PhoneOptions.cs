using System.ComponentModel.DataAnnotations;
using TemplateName.Modules.Auth.Domain.Verification;

namespace TemplateName.Modules.Auth.Application.Phone;

/// <summary>
/// The phone number routes (section <c>Auth:Phone</c>), validated when the host starts. Off by default: while <see cref="Enabled"/> is
/// false the routes answer 404. Turn it on only where the module that consumes the requested event texts the codes.
/// </summary>
internal sealed class PhoneOptions : IValidatableObject
{
    internal const string SectionName = "Auth:Phone";

    /// <summary>Whether <c>POST</c> and <c>DELETE</c> <c>/api/v1/auth/me/phone</c> and <c>POST .../confirm</c> answer.</summary>
    public bool Enabled { get; set; }

    /// <summary>ISO 3166 region codes (<c>MY</c>, <c>SG</c>, ...) whose numbers are accepted; empty accepts every region.</summary>
    public string[] AllowedRegions { get; set; } = [];

    /// <summary>How long a code is valid.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "00:30:00")]
    public TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The least time between two codes for one user.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:10:00")]
    public TimeSpan ResendCooldown { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>The most codes one user is sent in 24 hours.</summary>
    [Range(1, 50)]
    public int MaxCodesPerDay { get; set; } = 5;

    /// <summary>The wrong tries that end a code.</summary>
    [Range(1, 10)]
    public int MaxAttempts { get; set; } = 5;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var region in AllowedRegions ?? [])
        {
            if (region is null || !PhoneNumberRules.IsKnownRegion(region))
            {
                yield return new ValidationResult(
                    $"{nameof(AllowedRegions)} holds '{region}', which is not an upper-case ISO 3166 region code with phone numbers.",
                    [nameof(AllowedRegions)]);
            }
        }
    }

    /// <summary><see cref="AllowedRegions"/> as a set, for <see cref="PhoneNumberRules.TryNormalize"/>.</summary>
    internal IReadOnlySet<string> AllowedRegionSet() => new HashSet<string>(AllowedRegions ?? [], StringComparer.Ordinal);
}
