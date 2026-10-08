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

    [Theory]
    [MemberData(nameof(Assemblies.ModuleNames), MemberType = typeof(Assemblies))]
    public void Module_domain_does_not_depend_on_other_layers_or_frameworks(string moduleName)
    {
        var domainTypes = Types.InAssembly(Assemblies.Module(moduleName)).That().ResideInNamespaceStartingWith($"{moduleName}.Domain");
        domainTypes.GetTypes().ShouldNotBeEmpty();

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
