using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace TemplateName.Web.Common.Localization;

/// <summary>Request localization settings (section <c>Localization</c>). Only the UI culture (message language) varies per request.</summary>
public sealed class ApiLocalizationOptions : IValidatableObject
{
    internal const string SectionName = "Localization";

    /// <summary>The fallback UI culture and the fixed formatting culture of every request and of background work.</summary>
    [Required]
    public string DefaultCulture { get; set; } = "en";

    /// <summary>The languages a client may ask for with <c>Accept-Language</c>. A configured list replaces this default.</summary>
    [MinLength(1)]
    public string[] SupportedUICultures { get; set; } = ["en", "ms", "zh-Hans"];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!SupportedUICultures.Contains(DefaultCulture, StringComparer.OrdinalIgnoreCase))
        {
            yield return new ValidationResult(
                $"The default culture '{DefaultCulture}' must be one of the supported UI cultures ({string.Join(", ", SupportedUICultures)}).",
                [nameof(DefaultCulture)]);
        }

        foreach (var name in SupportedUICultures)
        {
            if (!IsPredefinedCulture(name))
            {
                yield return new ValidationResult(
                    $"'{name}' is not a culture this runtime knows. Check the name, and that the host has ICU (InvariantGlobalization off).",
                    [nameof(SupportedUICultures)]);
            }
        }
    }

    private static bool IsPredefinedCulture(string name)
    {
        try
        {
            _ = CultureInfo.GetCultureInfo(name, predefinedOnly: true);
            return true;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}
