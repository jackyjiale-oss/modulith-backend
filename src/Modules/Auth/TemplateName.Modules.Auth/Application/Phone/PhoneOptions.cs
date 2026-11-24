using System.ComponentModel.DataAnnotations;
using TemplateName.Modules.Auth.Domain.Roles;
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

    /// <summary>Turns the two phone sign-in routes on (otherwise they answer 404). Needs <see cref="Enabled"/>.</summary>
    public bool SignInEnabled { get; set; }

    /// <summary>How long a sign-in code is valid.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "00:10:00")]
    public TimeSpan SignInCodeLifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Roles whose members cannot sign in by SMS without a confirmed TOTP method, compared on normalized names. Null (not configured)
    /// means <see cref="DefaultDeniedRoles"/>; a configured list replaces it (the configuration binder appends to an array that already
    /// has items, so the default is not the property's initial value). A name no role has denies nobody.
    /// </summary>
    public string[]? SignInDeniedRoles { get; set; }

    internal static IReadOnlyList<string> DefaultDeniedRoles { get; } = ["SuperAdmin", "Admin"];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SignInEnabled && !Enabled)
        {
            yield return new ValidationResult(
                $"{nameof(SignInEnabled)} needs {nameof(Enabled)}: phone sign-in texts a code to the account's verified number.",
                [nameof(SignInEnabled), nameof(Enabled)]);
        }

        foreach (var role in SignInDeniedRoles ?? [])
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                yield return new ValidationResult($"{nameof(SignInDeniedRoles)} holds a blank role name.", [nameof(SignInDeniedRoles)]);
            }
        }
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
    /// <summary>The normalized names of <see cref="SignInDeniedRoles"/>, or of the defaults when it is not configured.</summary>
    internal IReadOnlySet<string> DeniedRoleSet() =>
        new HashSet<string>(
            (SignInDeniedRoles ?? DefaultDeniedRoles).Where(role => !string.IsNullOrWhiteSpace(role)).Select(Role.NormalizeName),
            StringComparer.Ordinal);

    internal IReadOnlySet<string> AllowedRegionSet() => new HashSet<string>(AllowedRegions ?? [], StringComparer.Ordinal);
}
