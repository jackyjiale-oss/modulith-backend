using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Notifications.Infrastructure.Templates;

/// <summary>Values every notification template can use (section <c>Notifications:Templates</c>).</summary>
internal sealed class TemplateOptions
{
    internal const string SectionName = "Notifications:Templates";

    /// <summary>The product's name as recipients know it, available to every template as <c>product_name</c> and shown in the email footer.</summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(100)]
    public string ProductName { get; set; } = "TemplateName";
}
