namespace TemplateName.ArchitectureTests;

public sealed class ModuleBoundaryTests
{
    [Theory]
    [MemberData(nameof(Assemblies.ModuleNames), MemberType = typeof(Assemblies))]
    public void Modules_do_not_reference_other_modules_except_contracts(string moduleName)
    {
        var illegalReferences = Assemblies.Module(moduleName)
            .GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .Where(name => name.StartsWith("TemplateName.Modules.", StringComparison.Ordinal))
            .Where(name => name != moduleName && !name.EndsWith(".Contracts", StringComparison.Ordinal))
            .ToList();

        illegalReferences.ShouldBeEmpty();
    }
}
