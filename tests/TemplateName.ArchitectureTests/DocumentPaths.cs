using System.Text;

namespace TemplateName.ArchitectureTests;

/// <summary>
/// Where the module and building-block documents live, derived the same way by every documentation test. The integration tests link
/// this file, so <c>EndpointDocumentationTests</c> and <c>DocumentationTests</c> cannot drift apart.
/// </summary>
public static class DocumentPaths
{
    public static string DocsDirectory => Path.Combine(RepositoryPaths.Root, "docs");

    /// <summary>
    /// <c>docs/modules/{module}.md</c> with the module name in kebab case. Takes the module name (<c>LeaveManagement</c>, from the
    /// assembly) or its route segment (<c>leave-management</c>, already kebab case); both give <c>leave-management.md</c>.
    /// </summary>
    public static string ModuleDocument(string moduleName) => Path.Combine(DocsDirectory, "modules", $"{KebabCase(moduleName)}.md");

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
