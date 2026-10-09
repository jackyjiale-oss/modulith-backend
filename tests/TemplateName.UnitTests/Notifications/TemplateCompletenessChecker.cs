using System.Reflection;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Templates;

namespace TemplateName.UnitTests.Notifications;

/// <summary>
/// The template rules of ADR 0019 as a testable unit: <c>TemplateCompletenessTests</c> runs it over the production catalog and over
/// <see cref="TestNotificationTypeSource"/>, where it must find the problems planted there. Each method returns one line per problem;
/// an empty list means the rule holds. Templates are read in their exact culture, without the <c>en</c> fallback the renderer uses.
/// </summary>
internal sealed class TemplateCompletenessChecker(NotificationCatalog catalog)
{
    /// <summary>Every part of every default channel exists in every supported culture and parses.</summary>
    public IReadOnlyList<string> MissingParts(string typeCode)
    {
        var problems = new List<string>();
        foreach (var (resourceName, template) in Parts(typeCode))
        {
            if (template is null)
            {
                problems.Add($"{resourceName} is missing");
            }
            else if (template.HasErrors)
            {
                problems.Add($"{resourceName} does not parse: {string.Join("; ", template.Messages)}");
            }
        }

        return problems;
    }

    /// <summary>Each part uses the same variables in every culture as in <c>en</c>; <c>product_name</c> is free to appear or not.</summary>
    public IReadOnlyList<string> PlaceholderMismatches(string typeCode)
    {
        var problems = new List<string>();
        var type = Type(typeCode);
        var assembly = catalog.TemplateAssemblyOf(typeCode);

        foreach (var channel in type.DefaultChannels)
        {
            foreach (var part in EmbeddedTemplateStore.PartsOf(channel))
            {
                var english = Variables(assembly, EmbeddedTemplateStore.PartResourceName(assembly, channel, typeCode, RecipientCulture.Default, part));
                if (english is null)
                {
                    continue;
                }

                foreach (var culture in RecipientCulture.Supported.Where(culture => culture != RecipientCulture.Default))
                {
                    var resourceName = EmbeddedTemplateStore.PartResourceName(assembly, channel, typeCode, culture, part);
                    var variables = Variables(assembly, resourceName);
                    if (variables is not null && !variables.SetEquals(english))
                    {
                        problems.Add($"{resourceName} uses [{Join(variables)}] but the en template uses [{Join(english)}]");
                    }
                }
            }
        }

        return problems;
    }

    /// <summary>Templates use only the type's variables, its secret variables and <c>product_name</c>.</summary>
    public IReadOnlyList<string> UndeclaredVariables(string typeCode)
    {
        var type = Type(typeCode);
        var declared = type.Variables.Concat(type.SecretVariables).Append(TemplateVariables.ProductName).ToHashSet(StringComparer.Ordinal);
        declared.Remove(TemplateVariables.This);

        return Parts(typeCode)
            .Where(entry => entry.Template is { HasErrors: false })
            .SelectMany(entry => TemplateVariables.Collect(entry.Template!)
                .Where(variable => !declared.Contains(variable))
                .Select(variable => $"{entry.ResourceName} uses the undeclared variable '{variable}'"))
            .ToList();
    }

    /// <summary>Decision D4: secret variables appear only in the email text and HTML parts, never in a subject or an in-app part (<c>this</c> counts as all of them).</summary>
    public IReadOnlyList<string> SecretVariableMisuse(string typeCode)
    {
        var type = Type(typeCode);
        var assembly = catalog.TemplateAssemblyOf(typeCode);
        var problems = new List<string>();

        foreach (var (channel, part) in new[] { (NotificationChannel.Email, "subject"), (NotificationChannel.InApp, "title"), (NotificationChannel.InApp, "body") })
        {
            if (!type.DefaultChannels.Contains(channel))
            {
                continue;
            }

            foreach (var culture in RecipientCulture.Supported)
            {
                var resourceName = EmbeddedTemplateStore.PartResourceName(assembly, channel, typeCode, culture, part);
                var variables = Variables(assembly, resourceName) ?? [];

                // 'this' reads the whole model, so it uses every secret variable without naming one.
                var secrets = (variables.Contains(TemplateVariables.This) ? type.SecretVariables : variables.Where(type.SecretVariables.Contains))
                    .Order(StringComparer.Ordinal)
                    .ToList();
                if (secrets.Count > 0)
                {
                    problems.Add($"{resourceName} uses the secret variables [{string.Join(", ", secrets)}]");
                }
            }
        }

        return problems;
    }

    /// <summary>Every embedded <c>.scriban</c> resource in <paramref name="assemblies"/> is a part of a declared type and channel, or a layout.</summary>
    public IReadOnlyList<string> OrphanResources(IEnumerable<Assembly> assemblies)
    {
        var expected = new HashSet<string>(RecipientCulture.Supported.Select(EmbeddedTemplateStore.LayoutResourceName), StringComparer.Ordinal);
        foreach (var type in catalog.All)
        {
            var assembly = catalog.TemplateAssemblyOf(type.Code);
            expected.UnionWith(
                from channel in type.DefaultChannels
                from part in EmbeddedTemplateStore.PartsOf(channel)
                from culture in RecipientCulture.Supported
                select EmbeddedTemplateStore.PartResourceName(assembly, channel, type.Code, culture, part));
        }

        return assemblies
            .Distinct()
            .SelectMany(assembly => assembly.GetManifestResourceNames())
            .Where(name => name.EndsWith(EmbeddedTemplateStore.FileExtension, StringComparison.Ordinal) && !expected.Contains(name))
            .Order(StringComparer.Ordinal)
            .Select(name => $"{name} belongs to no declared type, channel and culture, and is not a layout")
            .ToList();
    }

    /// <summary>Every supported culture has an email layout that parses and uses only <c>content</c>, <c>subject</c> and <c>product_name</c>.</summary>
    public IReadOnlyList<string> LayoutProblems()
    {
        var problems = new List<string>();
        foreach (var culture in RecipientCulture.Supported)
        {
            var resourceName = EmbeddedTemplateStore.LayoutResourceName(culture);
            var template = EmbeddedTemplateStore.Parse(EmbeddedTemplateStore.LayoutAssembly, resourceName);
            if (template is null || template.HasErrors)
            {
                problems.Add($"{resourceName} is missing or does not parse");
                continue;
            }

            var unknown = TemplateVariables.Collect(template).Where(variable => !TemplateVariables.LayoutVariables.Contains(variable)).ToList();
            if (unknown.Count > 0)
            {
                problems.Add($"{resourceName} uses [{string.Join(", ", unknown)}]; a layout sees only [{Join(TemplateVariables.LayoutVariables)}]");
            }

            if (!TemplateVariables.Collect(template).Contains(TemplateVariables.Content))
            {
                problems.Add($"{resourceName} does not print '{TemplateVariables.Content}'");
            }
        }

        return problems;
    }

    private NotificationTypeDefinition Type(string typeCode) =>
        catalog.Find(typeCode) ?? throw new InvalidOperationException($"Notification type '{typeCode}' is not in the catalog.");

    private IEnumerable<(string ResourceName, Scriban.Template? Template)> Parts(string typeCode)
    {
        var type = Type(typeCode);
        var assembly = catalog.TemplateAssemblyOf(typeCode);

        return
            from channel in type.DefaultChannels
            from part in EmbeddedTemplateStore.PartsOf(channel)
            from culture in RecipientCulture.Supported
            let resourceName = EmbeddedTemplateStore.PartResourceName(assembly, channel, typeCode, culture, part)
            select (resourceName, EmbeddedTemplateStore.Parse(assembly, resourceName));
    }

    private static HashSet<string>? Variables(Assembly assembly, string resourceName)
    {
        var template = EmbeddedTemplateStore.Parse(assembly, resourceName);
        if (template is null || template.HasErrors)
        {
            return null;
        }

        var variables = TemplateVariables.Collect(template).ToHashSet(StringComparer.Ordinal);
        variables.Remove(TemplateVariables.ProductName);
        return variables;
    }

    private static string Join(IEnumerable<string> names) => string.Join(", ", names.Order(StringComparer.Ordinal));
}
