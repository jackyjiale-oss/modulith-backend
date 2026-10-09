using System.Text;

namespace TemplateName.ArchitectureTests;

/// <summary>
/// Where the module and building-block documents live, derived the same way by every documentation test. The integration tests link
/// this file, so <c>EndpointDocumentationTests</c> and <c>DocumentationTests</c> cannot drift apart.
/// </summary>
public static class DocumentPaths
{
    private const string ApiPrefix = "/api/v1/";
    private const string AdminSegment = "admin";

    public static string DocsDirectory => Path.Combine(RepositoryPaths.Root, "docs");

    /// <summary>
    /// <c>docs/modules/{module}.md</c> with the module name in kebab case. Takes the module name (<c>LeaveManagement</c>, from the
    /// assembly) or its route segment (<c>leave-management</c>, already kebab case); both give <c>leave-management.md</c>.
    /// </summary>
    public static string ModuleDocument(string moduleName) => Path.Combine(DocsDirectory, "modules", $"{KebabCase(moduleName)}.md");

    /// <summary>
    /// The module an API route belongs to, as its route segment: the segment after <c>/api/v1/</c> (<c>/api/v1/sample/…</c> →
    /// <c>sample</c>), or for an administration route the segment after <c>/api/v1/admin/</c> (<c>/api/v1/admin/auth/users</c> →
    /// <c>auth</c>). Case-insensitive, like routing.
    /// </summary>
    /// <exception cref="ArgumentException">The route does not start with <c>/api/v1/</c>.</exception>
    public static string ModuleOfApiRoute(string route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (!route.StartsWith(ApiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{route}' is not an API route ({ApiPrefix}...).", nameof(route));
        }

        var segments = route[ApiPrefix.Length..].Split('/');
        return segments.Length > 1 && string.Equals(segments[0], AdminSegment, StringComparison.OrdinalIgnoreCase)
            ? segments[1]
            : segments[0];
    }

    /// <summary><c>SharedKernel</c> → <c>shared-kernel</c>, <c>Application.Common</c> → <c>application-common</c>; kebab case stays as it is.</summary>
    public static string KebabCase(string name)
    {
        var builder = new StringBuilder();
        foreach (var character in name)
        {
            if (character == '.')
            {
                builder.Append('-');
            }
            else if (char.IsUpper(character) && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-').Append(char.ToLowerInvariant(character));
            }
            else
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }
}
