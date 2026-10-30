using System.Text.Json;
using System.Text.RegularExpressions;

namespace TemplateName.ArchitectureTests;

/// <summary>
/// Reads the version register of <c>docs/api-compatibility.md</c> and checks it against the API versions the OpenAPI document serves:
/// a served version needs a row, and an <c>Active</c> or <c>Deprecated</c> row needs a served version.
/// </summary>
internal static partial class ApiVersionRegister
{
    private const string Heading = "## Version register";

    private const string Active = "Active";
    private const string Deprecated = "Deprecated";
    private const string RetirementAnnounced = "Retirement announced";
    private const string Retired = "Retired";

    private static readonly string[] Statuses = [Active, Deprecated, RetirementAnnounced, Retired];

    /// <summary>
    /// The data rows of the table under <c>## Version register</c>, up to the next <c>## </c> heading, as (version, status): the version
    /// lower-cased and trimmed (<c>v1</c>), the status trimmed. The header and separator rows are skipped.
    /// </summary>
    public static IReadOnlyList<(string Version, string Status)> Parse(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var rows = new List<(string Version, string Status)>();
        var isInRegister = false;
        var isHeaderSeen = false;

        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                isInRegister = line.TrimEnd() == Heading;
                isHeaderSeen = false;
                continue;
            }

            var text = line.Trim();
            if (!isInRegister || !text.StartsWith('|'))
            {
                continue;
            }

            if (!isHeaderSeen)
            {
                isHeaderSeen = true;
                continue;
            }

            var cells = text.Trim('|').Split('|').Select(cell => cell.Trim()).ToList();
            if (cells.Count >= 2 && !cells.All(IsSeparatorCell))
            {
                rows.Add((cells[0].ToLowerInvariant(), cells[1]));
            }
        }

        return rows;
    }

    /// <summary>The distinct versions (<c>v1</c>) of the paths of an OpenAPI document that start with <c>/api/v{N}/</c>.</summary>
    public static IReadOnlySet<string> PathVersions(string openApiJson)
    {
        ArgumentNullException.ThrowIfNull(openApiJson);
        using var document = JsonDocument.Parse(openApiJson);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
        {
            foreach (var path in paths.EnumerateObject())
            {
                var match = ApiPath().Match(path.Name);
                if (match.Success)
                {
                    versions.Add($"v{match.Groups[1].Value}");
                }
            }
        }

        return versions;
    }

    /// <summary>What is wrong between the register and the served versions; empty when they agree.</summary>
    public static IReadOnlyList<string> Violations(IReadOnlyList<(string Version, string Status)> register, IReadOnlySet<string> pathVersions)
    {
        ArgumentNullException.ThrowIfNull(register);
        ArgumentNullException.ThrowIfNull(pathVersions);
        var violations = new List<string>();

        foreach (var version in pathVersions.Order(StringComparer.Ordinal).Where(version => register.All(row => row.Version != version)))
        {
            violations.Add($"The OpenAPI document serves /api/{version}/ but the version register has no row for {version}");
        }

        foreach (var (version, status) in register)
        {
            var canonical = Statuses.FirstOrDefault(known => string.Equals(known, status, StringComparison.OrdinalIgnoreCase));
            var isServed = pathVersions.Contains(version);
            if (canonical is null)
            {
                violations.Add($"The version register gives {version} the status '{status}'; expected one of {string.Join(", ", Statuses)}");
            }
            else if (canonical is Active or Deprecated && !isServed)
            {
                violations.Add($"The version register has {version} as {canonical} but the OpenAPI document serves no /api/{version}/ path");
            }
            else if (canonical == Retired && isServed)
            {
                violations.Add($"The version register has {version} as {Retired} but the OpenAPI document still serves /api/{version}/ paths");
            }
        }

        return violations;
    }

    private static bool IsSeparatorCell(string cell) => cell.Length > 0 && cell.All(character => character is '-' or ':');

    [GeneratedRegex("^/api/v([0-9]+)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ApiPath();
}
