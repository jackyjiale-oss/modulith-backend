using Scriban;

namespace TemplateName.Modules.Notifications.Infrastructure.Templates;

/// <summary>A parsed template resource with the variables it reads (empty when it does not parse). Immutable, so renders share it.</summary>
internal sealed class StoredTemplate(string resourceName, Template template)
{
    /// <summary>The manifest resource the template was read from.</summary>
    public string ResourceName { get; } = resourceName;

    /// <summary>The parsed template; check <see cref="Template.HasErrors"/> before rendering it.</summary>
    public Template Template { get; } = template;

    /// <summary>The variables the template reads (<see cref="TemplateVariables.Collect"/>).</summary>
    public IReadOnlySet<string> Variables { get; } = template.HasErrors ? new HashSet<string>() : TemplateVariables.Collect(template);
}
