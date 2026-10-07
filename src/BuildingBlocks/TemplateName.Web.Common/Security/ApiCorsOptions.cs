using System.ComponentModel.DataAnnotations;

namespace TemplateName.Web.Common.Security;

/// <summary>CORS settings (section <c>Cors</c>). An empty list allows no cross-origin caller.</summary>
internal sealed class ApiCorsOptions : IValidatableObject
{
    internal const string SectionName = "Cors";

    /// <summary>Origins allowed to call the API with credentials, for example <c>https://app.example.com</c>. A wildcard is rejected.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (AllowedOrigins.Any(origin => origin.Trim() == "*"))
        {
            yield return new ValidationResult(
                "A wildcard origin (*) is not allowed because credentials are enabled; list each origin explicitly.",
                [nameof(AllowedOrigins)]);
        }
    }
}
