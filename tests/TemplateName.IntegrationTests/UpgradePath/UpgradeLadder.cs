using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TemplateName.IntegrationTests.UpgradePath;

/// <summary>One migration of a module's context. <paramref name="Name"/> is <paramref name="Id"/> without its timestamp.</summary>
internal sealed record MigrationRung(string Id, string Name, Type MigrationType);

/// <summary>The migrations of one context in the order they apply. <paramref name="Module"/> is the context's name without <c>DbContext</c>.</summary>
internal sealed record ModuleLadder(string Module, Type ContextType, IReadOnlyList<MigrationRung> Rungs);

/// <summary>
/// The upgrade-path ladder (docs/deployment/migrations.md): every module's migrations in order, and the seed and verify scripts that
/// belong to some of them. A seed is <c>Seeds/{Module}/{Name}.seed.sql</c>, run once the database is at that migration; its
/// <c>{Name}.verify.sql</c> runs once the database is at the latest one and returns a row for everything that went wrong.
/// </summary>
internal static class UpgradeLadder
{
    private const string AssemblyPrefix = "TemplateName.";
    private const string ResourceMarker = "UpgradePath.Seeds.";
    private const string SeedSuffix = ".seed.sql";
    private const string VerifySuffix = ".verify.sql";

    private static readonly Lazy<(IReadOnlyDictionary<string, string> Seeds, IReadOnlyDictionary<string, string> Verifies)> Scripts = new(ReadScripts);

    /// <summary>The seed scripts, keyed <c>{Module}/{Name}</c>.</summary>
    public static IReadOnlyDictionary<string, string> Seeds => Scripts.Value.Seeds;

    /// <summary>The verify scripts, keyed <c>{Module}/{Name}</c>.</summary>
    public static IReadOnlyDictionary<string, string> Verifies => Scripts.Value.Verifies;

    /// <summary>
    /// The migrations of every context in the API's assembly and the assemblies it references that belong to the solution (a migration is
    /// a type with both <c>[DbContext]</c> and <c>[Migration]</c>), ordered by module and by migration id.
    /// </summary>
    public static IReadOnlyList<ModuleLadder> Discover()
    {
        var apiAssembly = typeof(Program).Assembly;
        var assemblies = apiAssembly.GetReferencedAssemblies()
            .Where(name => name.Name is not null && name.Name.StartsWith(AssemblyPrefix, StringComparison.Ordinal))
            .Select(Assembly.Load)
            .Append(apiAssembly)
            .Distinct();

        return assemblies
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsSubclassOf(typeof(Migration)))
            .Select(type => (Type: type, Context: type.GetCustomAttribute<DbContextAttribute>(), Migration: type.GetCustomAttribute<MigrationAttribute>()))
            .Where(candidate => candidate.Context is not null && candidate.Migration is not null)
            .GroupBy(candidate => candidate.Context!.ContextType)
            .Select(group => new ModuleLadder(
                ModuleOf(group.Key),
                group.Key,
                [.. group
                    .Select(candidate => new MigrationRung(candidate.Migration!.Id, NameOf(candidate.Migration.Id), candidate.Type))
                    .OrderBy(rung => rung.Id, StringComparer.Ordinal)]))
            .OrderBy(ladder => ladder.Module, StringComparer.Ordinal)
            .ToList();
    }

    private static string ModuleOf(Type contextType) => contextType.Name.EndsWith("DbContext", StringComparison.Ordinal)
        ? contextType.Name[..^"DbContext".Length]
        : contextType.Name;

    private static string NameOf(string migrationId) => migrationId[(migrationId.IndexOf('_', StringComparison.Ordinal) + 1)..];

    /// <summary>
    /// Reads the scripts from the embedded resources. The resource name starts with the project's root namespace, which a generated project
    /// changes, so only what follows <c>UpgradePath.Seeds.</c> counts: <c>{Module}.{Name}.seed.sql</c> (a module name has no dot).
    /// </summary>
    private static (IReadOnlyDictionary<string, string> Seeds, IReadOnlyDictionary<string, string> Verifies) ReadScripts()
    {
        var assembly = typeof(UpgradeLadder).Assembly;
        var seeds = new Dictionary<string, string>(StringComparer.Ordinal);
        var verifies = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var marker = resource.IndexOf(ResourceMarker, StringComparison.Ordinal);
            if (marker < 0)
            {
                continue;
            }

            var relative = resource[(marker + ResourceMarker.Length)..];
            var (target, suffix) = relative.EndsWith(SeedSuffix, StringComparison.Ordinal)
                ? (seeds, SeedSuffix)
                : relative.EndsWith(VerifySuffix, StringComparison.Ordinal)
                    ? (verifies, VerifySuffix)
                    : throw new InvalidOperationException($"The embedded script '{resource}' is neither '*{SeedSuffix}' nor '*{VerifySuffix}'.");
            var moduleAndName = relative[..^suffix.Length];
            var dot = moduleAndName.IndexOf('.', StringComparison.Ordinal);
            if (dot <= 0 || dot == moduleAndName.Length - 1)
            {
                throw new InvalidOperationException($"The embedded script '{resource}' is not named 'Seeds/{{Module}}/{{Name}}{suffix}'.");
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"The embedded script '{resource}' cannot be read.");
            using var reader = new StreamReader(stream);
            target[$"{moduleAndName[..dot]}/{moduleAndName[(dot + 1)..]}"] = reader.ReadToEnd();
        }

        return (seeds, verifies);
    }
}
