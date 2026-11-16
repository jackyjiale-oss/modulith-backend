namespace TemplateName.ArchitectureTests;

/// <summary>
/// Every other architecture rule runs over <see cref="Assemblies.Modules"/> and <see cref="Assemblies.Contracts"/>, so a module project that exists on disk but is missing
/// from them would be silently ignored by all of those rules. This rule fails instead.
/// </summary>
public sealed class ModuleRegistrationTests
{
    [Fact]
    public void Every_module_project_on_disk_is_listed_in_Assemblies()
    {
        var modulesDirectory = Path.Combine(RepositoryPaths.Root, "src", "Modules");

        var unlisted = UnlistedProjects(
            modulesDirectory,
            Assemblies.Modules.Select(module => module.GetName().Name!),
            Assemblies.Contracts.Select(contracts => contracts.GetName().Name!));

        unlisted.ShouldBeEmpty(
            $"Add each of these to Assemblies.Modules or Assemblies.Contracts, or the architecture rules ignore it: {string.Join(", ", unlisted)}");
    }

    [Fact]
    public void An_unlisted_module_project_is_reported()
    {
        using var fixture = new ModulesFixture("X.Modules.Billing", "Billing");

        var unlisted = UnlistedProjects(fixture.Directory, listedModules: [], listedContracts: []);

        unlisted.ShouldBe(["X.Modules.Billing"]);
    }

    [Fact]
    public void An_unlisted_contracts_project_is_reported()
    {
        using var fixture = new ModulesFixture("X.Modules.Billing.Contracts", "Billing");

        var unlisted = UnlistedProjects(fixture.Directory, listedModules: ["X.Modules.Billing.Contracts"], listedContracts: []);

        unlisted.ShouldBe(["X.Modules.Billing.Contracts"]);
    }

    /// <summary>
    /// The names of the projects under <paramref name="modulesDirectory"/> (<c>{Module}/{Assembly}/{Assembly}.csproj</c>) that are in neither list, sorted ordinally.
    /// A project named <c>*.Contracts</c> is checked only against <paramref name="listedContracts"/>, every other project only against <paramref name="listedModules"/>.
    /// </summary>
    internal static IReadOnlyList<string> UnlistedProjects(string modulesDirectory, IEnumerable<string> listedModules, IEnumerable<string> listedContracts)
    {
        var modules = listedModules.ToHashSet(StringComparer.Ordinal);
        var contracts = listedContracts.ToHashSet(StringComparer.Ordinal);

        return
        [
            .. Directory.GetFiles(modulesDirectory, "*.csproj", SearchOption.AllDirectories)
                .Select(project => Path.GetFileNameWithoutExtension(project))
                .Where(name => !(name.EndsWith(".Contracts", StringComparison.Ordinal) ? contracts : modules).Contains(name))
                .Order(StringComparer.Ordinal),
        ];
    }

    /// <summary>A temporary <c>src/Modules</c> holding one empty project file at <c>{module}/{assembly}/{assembly}.csproj</c>, deleted on dispose.</summary>
    private sealed class ModulesFixture : IDisposable
    {
        public ModulesFixture(string assembly, string module)
        {
            Directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var projectDirectory = Path.Combine(Directory, module, assembly);
            System.IO.Directory.CreateDirectory(projectDirectory);
            File.WriteAllText(Path.Combine(projectDirectory, $"{assembly}.csproj"), string.Empty);
        }

        public string Directory { get; }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
