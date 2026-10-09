using System.Collections.Concurrent;
using System.Reflection;
using Scriban;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Notifications;

namespace TemplateName.Modules.Notifications.Infrastructure.Templates;

/// <summary>
/// Reads notification templates embedded as manifest resources (ADR 0019) and keeps each one parsed for the life of the process
/// (singleton, thread-safe; every resource is read and parsed once). A part of type <c>auth.password_changed</c> is
/// <c>{assemblyName}.Templates.{Email|InApp}.auth.password_changed.{culture}.{part}.scriban</c> in the assembly of the source that
/// declared the type (<see cref="NotificationCatalog.TemplateAssemblyOf"/>), which is the name MSBuild gives a file in a
/// <c>Templates/{Email|InApp}/</c> folder at the project root. Email layouts are
/// <c>{assemblyName}.Templates.Email._layout.{culture}.html.scriban</c> in this module's assembly.
/// </summary>
internal sealed class EmbeddedTemplateStore(NotificationCatalog catalog)
{
    /// <summary>The file extension of every template resource.</summary>
    public const string FileExtension = ".scriban";

    private static readonly string[] EmailParts = ["subject", "text", "html"];
    private static readonly string[] InAppParts = ["title", "body"];

    private readonly ConcurrentDictionary<(Assembly Assembly, string ResourceName), Lazy<StoredTemplate?>> _templates = new();

    /// <summary>The assembly that holds the email layouts: this module's.</summary>
    public static Assembly LayoutAssembly { get; } = typeof(EmbeddedTemplateStore).Assembly;

    /// <summary>The parts a channel's templates have: <c>subject</c>, <c>text</c> and <c>html</c> for email; <c>title</c> and <c>body</c> in-app.</summary>
    public static IReadOnlyList<string> PartsOf(NotificationChannel channel) => channel switch
    {
        NotificationChannel.Email => EmailParts,
        NotificationChannel.InApp => InAppParts,
        _ => [],
    };

    /// <summary>The manifest resource name of one part of a type's template in <paramref name="assembly"/>.</summary>
    public static string PartResourceName(Assembly assembly, NotificationChannel channel, string typeCode, string culture, string part)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        return $"{assembly.GetName().Name}.Templates.{channel}.{typeCode}.{culture}.{part}{FileExtension}";
    }

    /// <summary>The manifest resource name of the email layout of <paramref name="culture"/> in <see cref="LayoutAssembly"/>.</summary>
    public static string LayoutResourceName(string culture) =>
        $"{LayoutAssembly.GetName().Name}.Templates.{NotificationChannel.Email}._layout.{culture}.html{FileExtension}";

    /// <summary>
    /// Reads and parses <paramref name="resourceName"/> without caching; <see langword="null"/> when <paramref name="assembly"/> has no
    /// such resource. The result may have parse errors (<see cref="Template.HasErrors"/>). Line endings become <c>\n</c>, so a template
    /// renders the same whatever a checkout did to its file.
    /// </summary>
    public static Template? Parse(Assembly assembly, string resourceName)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);

        return Template.Parse(reader.ReadToEnd().ReplaceLineEndings("\n"), resourceName);
    }

    /// <summary>
    /// The <paramref name="part"/> template of <paramref name="typeCode"/> for <paramref name="channel"/> in <paramref name="culture"/>,
    /// else in <c>en</c> (an unsupported culture goes straight to <c>en</c>). A template that exists but does not parse is an error, not
    /// a reason to fall back.
    /// </summary>
    /// <exception cref="TemplateRenderException">The type is not declared, the template exists in neither culture, or it does not parse.</exception>
    public StoredTemplate GetPart(string typeCode, NotificationChannel channel, string culture, string part)
    {
        if (catalog.Find(typeCode) is null)
        {
            throw new TemplateRenderException(typeCode, channel, culture, "the type is not declared by any notification type source");
        }

        var assembly = catalog.TemplateAssemblyOf(typeCode);
        var template = Find(assembly, culture, candidate => PartResourceName(assembly, channel, typeCode, candidate, part))
            ?? throw new TemplateRenderException(
                typeCode,
                channel,
                culture,
                $"there is no '{part}' template for {Cultures(culture)} ({PartResourceName(assembly, channel, typeCode, culture, part)})");

        return Parsed(template, typeCode, channel, culture);
    }

    /// <summary>The email layout for <paramref name="culture"/>, else for <c>en</c>; <paramref name="typeCode"/> only names the type in an exception.</summary>
    /// <exception cref="TemplateRenderException">The layout exists in neither culture, or it does not parse.</exception>
    public StoredTemplate GetLayout(string typeCode, string culture)
    {
        var template = Find(LayoutAssembly, culture, LayoutResourceName)
            ?? throw new TemplateRenderException(
                typeCode,
                NotificationChannel.Email,
                culture,
                $"there is no email layout for {Cultures(culture)} ({LayoutResourceName(culture)})");

        return Parsed(template, typeCode, NotificationChannel.Email, culture);
    }

    private static string[] Candidates(string culture) =>
        culture != RecipientCulture.Default && RecipientCulture.Supported.Contains(culture, StringComparer.Ordinal)
            ? [culture, RecipientCulture.Default]
            : [RecipientCulture.Default];

    private static string Cultures(string culture) => $"culture {string.Join(" or ", Candidates(culture).Select(candidate => $"'{candidate}'"))}";

    private StoredTemplate? Find(Assembly assembly, string culture, Func<string, string> resourceNameOf)
    {
        foreach (var candidate in Candidates(culture))
        {
            if (_templates.GetOrAdd((assembly, resourceNameOf(candidate)), Load).Value is { } template)
            {
                return template;
            }
        }

        return null;
    }

    private static Lazy<StoredTemplate?> Load((Assembly Assembly, string ResourceName) key) =>
        new(() => Parse(key.Assembly, key.ResourceName) is { } template ? new StoredTemplate(key.ResourceName, template) : null);

    private static StoredTemplate Parsed(StoredTemplate template, string typeCode, NotificationChannel channel, string culture)
    {
        // Parser messages quote the template's own text and position, never a variable value: nothing has been rendered yet.
        return template.Template.HasErrors
            ? throw new TemplateRenderException(typeCode, channel, culture, $"{template.ResourceName} does not parse: {string.Join("; ", template.Template.Messages)}")
            : template;
    }
}
