using NetArchTest.Rules;

namespace TemplateName.ArchitectureTests;

public sealed class LayeringTests
{
    [Fact]
    public void SharedKernel_has_no_framework_or_project_dependencies()
    {
        var result = Types.InAssembly(Assemblies.SharedKernel)
            .Should()
            .NotHaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "TemplateName.Application",
                "TemplateName.Infrastructure",
                "TemplateName.Web")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Application_common_does_not_depend_on_infrastructure_or_web()
    {
        var result = Types.InAssembly(Assemblies.ApplicationCommon)
            .Should()
            .NotHaveDependencyOnAny("TemplateName.Infrastructure", "TemplateName.Web")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    //#if (caching == "redis")
    [Fact]
    public void Only_the_host_references_Platform_Redis()
    {
        // ADR 0027: the optional Redis project is referenced by the host alone, so a solution without Redis deletes one project and one
        // line of Program.cs. Checked twice: in the compiled references and in the project files (a reference nothing uses is dropped
        // from the compiled assembly but would still pull the packages in).
        var redis = Assemblies.PlatformRedis.GetName().Name!;
        var api = Assemblies.Api.GetName().Name!;

        var referencingAssemblies = Assemblies.All
            .Concat(Assemblies.Contracts)
            .Where(assembly => assembly != Assemblies.PlatformRedis)
            .Where(assembly => assembly.GetReferencedAssemblies().Any(reference => reference.Name == redis))
            .Select(assembly => assembly.GetName().Name)
            .ToList();
        var referencingProjects = Directory.GetFiles(Path.Combine(RepositoryPaths.Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(project => File.ReadAllText(project).Contains($"{redis}.csproj", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .ToList();

        referencingAssemblies.ShouldBe([api]);
        referencingProjects.ShouldBe([api]);
    }

    //#endif
    [Fact]
    public void At_least_one_module_has_domain_types()
    {
        // Keeps the per-module domain rule from passing vacuously: a rename of the Domain namespace would leave every module without types.
        var modulesWithDomainTypes = Assemblies.Modules
            .Where(assembly => Types.InAssembly(assembly).That().ResideInNamespaceStartingWith($"{assembly.GetName().Name}.Domain").GetTypes().Any())
            .ToList();

        modulesWithDomainTypes.ShouldNotBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Assemblies.ModuleNames), MemberType = typeof(Assemblies))]
    public void Module_domain_does_not_depend_on_other_layers_or_frameworks(string moduleName)
    {
        var domainTypes = Types.InAssembly(Assemblies.Module(moduleName)).That().ResideInNamespaceStartingWith($"{moduleName}.Domain");
        if (!domainTypes.GetTypes().Any())
        {
            // A module with no domain types yet (a freshly scaffolded one) is not a failure; At_least_one_module_has_domain_types guards the rule.
            return;
        }

        var result = domainTypes
            .Should()
            .NotHaveDependencyOnAny(
                $"{moduleName}.Application",
                $"{moduleName}.Infrastructure",
                $"{moduleName}.Endpoints",
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [MemberData(nameof(Assemblies.ModuleNames), MemberType = typeof(Assemblies))]
    public void Module_application_does_not_depend_on_infrastructure_or_endpoints(string moduleName)
    {
        // Dapper is deliberately not forbidden: queries read through IDbConnectionFactory.
        var applicationTypes = Types.InAssembly(Assemblies.Module(moduleName)).That().ResideInNamespaceStartingWith($"{moduleName}.Application");
        applicationTypes.GetTypes().ShouldNotBeEmpty();

        var result = applicationTypes
            .Should()
            .NotHaveDependencyOnAny($"{moduleName}.Infrastructure", $"{moduleName}.Endpoints")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }
}
