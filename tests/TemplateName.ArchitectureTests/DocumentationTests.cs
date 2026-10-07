using System.Reflection;
using System.Text;

namespace TemplateName.ArchitectureTests;

/// <summary>Enforces review Section 11.3: every module and building block has a document, and module documents keep their outline and error codes.</summary>
public sealed class DocumentationTests
{
    private const string ModuleNameSeparator = ".Modules.";

    private static readonly string[] RequiredModuleSections =
    [
        "Purpose and boundaries",
        "Endpoints",
        "Domain model",
        "Error codes",
        "Events",
        "Configuration",
        "Data",
        "Background processing",
        "Observability",
        "Testing",
    ];

    private static string DocsDirectory => Path.Combine(RepositoryPaths.Root, "docs");

    [Fact]
    public void Every_module_has_a_document()
    {
        var missing = Assemblies.Modules
            .Select(ModuleDocumentPath)
            .Where(path => !File.Exists(path))
            .ToList();

        Assemblies.Modules.ShouldNotBeEmpty();
        missing.ShouldBeEmpty(string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Module_documents_have_required_sections_in_order()
    {
        var documents = Assemblies.Modules
            .Select(ModuleDocumentPath)
            .Append(Path.Combine(DocsDirectory, "modules", "_template.md"))
            .ToList();
        var failures = new List<string>();

        foreach (var path in documents)
        {
            var required = Headings(File.ReadAllText(path)).Where(RequiredModuleSections.Contains).ToList();
            if (!required.SequenceEqual(RequiredModuleSections))
            {
                failures.Add($"{Path.GetFileName(path)} has [{string.Join(", ", required)}]; expected each of [{string.Join(", ", RequiredModuleSections)}] once, in that order");
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Module_documents_list_every_error_code()
    {
        var failures = new List<string>();
        var codeCount = 0;

        foreach (var module in Assemblies.Modules)
        {
            var document = File.ReadAllText(ModuleDocumentPath(module));
            var codes = ErrorCatalog.Collect(module).Select(error => error.Code).Distinct().ToList();
            codeCount += codes.Count;

            failures.AddRange(codes
                .Where(code => !document.Contains($"`{code}`", StringComparison.Ordinal))
                .Select(code => $"{Path.GetFileName(ModuleDocumentPath(module))} does not list `{code}`"));
        }

        codeCount.ShouldBeGreaterThan(0);
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Every_building_block_has_a_document()
    {
        var root = RootNamespace();
        var expected = Assemblies.BuildingBlocks
            .Select(assembly => KebabCase(assembly.GetName().Name![(root.Length + 1)..]))
            .ToList();

        expected.ShouldBe(["shared-kernel", "application-common", "infrastructure-common", "web-common"], ignoreOrder: true);
        var missing = expected
            .Select(name => Path.Combine(DocsDirectory, "building-blocks", $"{name}.md"))
            .Where(path => !File.Exists(path))
            .ToList();
        missing.ShouldBeEmpty(string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void Every_adr_is_listed_in_the_docs_index_and_the_architecture_overview()
    {
        var adrs = Directory.GetFiles(Path.Combine(DocsDirectory, "adr"), "*.md").Select(Path.GetFileName).ToList();
        string[] indexes = [Path.Combine(DocsDirectory, "README.md"), Path.Combine(DocsDirectory, "architecture", "overview.md")];
        var failures = new List<string>();

        foreach (var index in indexes)
        {
            var text = File.ReadAllText(index);
            failures.AddRange(adrs
                .Where(adr => !text.Contains($"adr/{adr})", StringComparison.Ordinal))
                .Select(adr => $"{Path.GetRelativePath(DocsDirectory, index)} does not link adr/{adr}"));
        }

        adrs.ShouldNotBeEmpty();
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Generated_project_files_exist()
    {
        // Generated projects have neither .template.config nor build/template-content.
        Assert.SkipUnless(Directory.Exists(Path.Combine(RepositoryPaths.Root, ".template.config")), "Only meaningful in the template repository.");

        var templateContent = Path.Combine(RepositoryPaths.Root, "build", "template-content");
        string[] files = ["README.md", "CHANGELOG.md", "CLAUDE.md", ".release-please-manifest.json"];
        var missing = files.Where(file => !File.Exists(Path.Combine(templateContent, file))).ToList();

        missing.ShouldBeEmpty(string.Join(", ", missing));
        File.ReadAllText(Path.Combine(templateContent, "README.md")).ShouldContain("TemplateName");
        File.ReadAllText(Path.Combine(templateContent, "CLAUDE.md")).ShouldContain("TemplateName");
    }

    /// <summary><c>docs/modules/{module}.md</c>, where the module name is the assembly name after <c>.Modules.</c>, in lower case.</summary>
    private static string ModuleDocumentPath(Assembly module)
    {
        var name = module.GetName().Name!;
        var moduleName = name[(name.IndexOf(ModuleNameSeparator, StringComparison.Ordinal) + ModuleNameSeparator.Length)..];
        return Path.Combine(DocsDirectory, "modules", $"{moduleName.ToLowerInvariant()}.md");
    }

    /// <summary>The project's root namespace (the placeholder <c>dotnet new</c> replaces), taken from the host assembly <c>{Root}.Api</c>.</summary>
    private static string RootNamespace()
    {
        var api = Assemblies.Api.GetName().Name!;
        return api[..api.LastIndexOf('.')];
    }

    /// <summary><c>SharedKernel</c> → <c>shared-kernel</c>, <c>Application.Common</c> → <c>application-common</c>.</summary>
    private static string KebabCase(string name)
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

    /// <summary>The text of every level-2 heading (<c>## …</c>) outside fenced code blocks, in document order.</summary>
    private static IEnumerable<string> Headings(string markdown)
    {
        var isInFence = false;
        foreach (var line in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                isInFence = !isInFence;
            }
            else if (!isInFence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                yield return line[3..].Trim();
            }
        }
    }
}
