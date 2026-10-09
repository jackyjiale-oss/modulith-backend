using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using TemplateName.SharedKernel;

namespace TemplateName.ArchitectureTests;

/// <summary>
/// Enforces the rules of the <c>*.Contracts</c> projects, the only surface another module may reference (ADR 0001, review Section 2.1):
/// public sealed records, enums and interfaces over the shared kernel, nothing else.
/// </summary>
public sealed class ContractsTests
{
    private const string SharedKernelName = "TemplateName.SharedKernel";
    private const string ModuleAssemblyPrefix = "TemplateName.Modules.";
    private const string IntegrationEventSuffix = "IntegrationEvent";

    [Fact]
    public void Contracts_reference_only_the_shared_kernel()
    {
        Assemblies.Contracts.ShouldNotBeEmpty();
        var failures = new List<string>();

        foreach (var contracts in Assemblies.Contracts)
        {
            // Compiled references: nothing but the shared kernel and the runtime's own assemblies.
            var references = contracts.GetReferencedAssemblies()
                .Select(reference => reference.Name!)
                .Where(name => name != "netstandard" && name != "mscorlib" && !name.StartsWith("System", StringComparison.Ordinal))
                .ToList();
            failures.AddRange(references.Where(name => name != SharedKernelName).Select(name => $"{contracts.GetName().Name} references {name}"));

            // The project file: one project reference (the shared kernel) and no package.
            var project = XDocument.Load(ProjectFilePath(contracts));
            var projectReferences = project.Descendants("ProjectReference")
                .Select(reference => Path.GetFileNameWithoutExtension((string?)reference.Attribute("Include") ?? string.Empty))
                .ToList();
            if (!projectReferences.SequenceEqual([SharedKernelName]))
            {
                failures.Add($"{contracts.GetName().Name}.csproj has project references [{string.Join(", ", projectReferences)}]; expected only {SharedKernelName}");
            }

            if (project.Descendants("PackageReference").Any() || project.Descendants("FrameworkReference").Any())
            {
                failures.Add($"{contracts.GetName().Name}.csproj has a package or framework reference");
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Contracts_types_are_public_and_sealed_records_enums_or_interfaces()
    {
        var failures = new List<string>();
        var types = DeclaredTypes();

        types.ShouldNotBeEmpty();
        foreach (var type in types)
        {
            if (type.IsNested || !type.IsPublic)
            {
                failures.Add($"{type.FullName} is not a public top-level type");
            }
            else if (!(type.IsInterface || type.IsEnum || (type.IsClass && type.IsSealed && !type.IsAbstract && IsRecord(type))))
            {
                failures.Add($"{type.FullName} is neither an interface, an enum nor a sealed record");
            }
        }

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Integration_events_are_named_IntegrationEvent_and_implement_the_interface()
    {
        var types = DeclaredTypes();
        var events = types.Where(type => type.IsClass && typeof(IIntegrationEvent).IsAssignableFrom(type)).ToList();
        var failures = new List<string>();

        events.ShouldNotBeEmpty();
        failures.AddRange(events
            .Where(type => !type.Name.EndsWith(IntegrationEventSuffix, StringComparison.Ordinal))
            .Select(type => $"{type.FullName} implements IIntegrationEvent but its name does not end with {IntegrationEventSuffix}"));
        failures.AddRange(types
            .Where(type => type.IsClass && type.Name.EndsWith(IntegrationEventSuffix, StringComparison.Ordinal) && !events.Contains(type))
            .Select(type => $"{type.FullName} is named *{IntegrationEventSuffix} but does not implement IIntegrationEvent"));

        // The interface is the contract: every event carries its id and instant first, so a consumer's inbox can key on the id.
        failures.AddRange(events
            .Where(type => !StartsWithIdAndOccurredAt(type))
            .Select(type => $"{type.FullName} does not declare 'Guid Id, DateTimeOffset OccurredAt' as its first two parameters"));

        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Contracts_do_not_reference_module_assemblies()
    {
        Assemblies.Contracts.ShouldNotBeEmpty();

        var illegalReferences = Assemblies.Contracts
            .SelectMany(contracts => contracts.GetReferencedAssemblies()
                .Where(reference => reference.Name!.StartsWith(ModuleAssemblyPrefix, StringComparison.Ordinal))
                .Select(reference => $"{contracts.GetName().Name} -> {reference.Name}"))
            .ToList();

        illegalReferences.ShouldBeEmpty();
    }

    /// <summary>Every type the contracts assemblies declare, without compiler-generated ones (closures, display classes).</summary>
    private static List<Type> DeclaredTypes() =>
        [.. Assemblies.Contracts
            .SelectMany(contracts => contracts.GetTypes())
            .Where(type => type.GetCustomAttribute<CompilerGeneratedAttribute>() is null)];

    /// <summary>A C# record declares a compiler-generated <c>&lt;Clone&gt;$</c> method.</summary>
    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    private static bool StartsWithIdAndOccurredAt(Type type)
    {
        var parameters = type.GetConstructors().Single(constructor => constructor.GetParameters().All(parameter => parameter.ParameterType != type)).GetParameters();
        return parameters.Length >= 2
            && parameters[0].Name == "Id" && parameters[0].ParameterType == typeof(Guid)
            && parameters[1].Name == "OccurredAt" && parameters[1].ParameterType == typeof(DateTimeOffset);
    }

    /// <summary>
    /// <c>src/Modules/{Module}/{Assembly}/{Assembly}.csproj</c> of a contracts assembly named <c>{Root}.Modules.{Module}.Contracts</c>.
    /// </summary>
    private static string ProjectFilePath(Assembly contracts)
    {
        var name = contracts.GetName().Name!;
        var moduleStart = name.IndexOf(".Modules.", StringComparison.Ordinal) + ".Modules.".Length;
        var module = name[moduleStart..name.LastIndexOf(".Contracts", StringComparison.Ordinal)];
        return Path.Combine(RepositoryPaths.Root, "src", "Modules", module, name, $"{name}.csproj");
    }
}
