using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Auth.Application.Passwords;

/// <summary>Password rules (section <c>Auth:Password</c>). There are no composition rules; length and breach status decide.</summary>
internal sealed class PasswordOptions : IValidatableObject
{
    internal const string SectionName = "Auth:Password";

    /// <summary>The shortest password accepted.</summary>
    [Range(8, 128)]
    public int MinLength { get; set; } = 12;

    /// <summary>The longest password accepted. It is checked before hashing, so a huge body cannot cost hashing time.</summary>
    [Range(8, 128)]
    public int MaxLength { get; set; } = 128;

    /// <summary>How many of the latest passwords may not be reused, the current one included.</summary>
    [Range(1, 24)]
    public int HistoryCount { get; set; } = 5;

    /// <summary>Whether a new password is checked against known breaches (HIBP k-anonymity). The check fails open.</summary>
    public bool CheckBreached { get; set; } = true;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MinLength > MaxLength)
        {
            yield return new ValidationResult(
                $"{nameof(MinLength)} may not be greater than {nameof(MaxLength)}.",
                [nameof(MinLength)]);
        }
    }
}
