using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetArchTest.Rules;
using TemplateName.SharedKernel;

namespace TemplateName.ArchitectureTests;

public sealed class ConventionTests
{
    [Fact]
    public void Handlers_are_sealed_and_not_public()
    {
        var failingTypeNames = new List<string>();
        var handlerCount = 0;

        foreach (var handlerInterface in Assemblies.HandlerInterfaces)
        {
            var handlers = Types.InAssemblies(Assemblies.All).That().AreClasses().And().ImplementInterface(handlerInterface);
            handlerCount += handlers.GetTypes().Count();

            var result = handlers.Should().BeSealed().And().NotBePublic().GetResult();
            failingTypeNames.AddRange(result.FailingTypeNames ?? []);
        }

        handlerCount.ShouldBeGreaterThan(0);
        failingTypeNames.ShouldBeEmpty(string.Join(", ", failingTypeNames));
    }

    [Fact]
    public void Domain_events_are_sealed_and_named_DomainEvent()
    {
        var domainEvents = Types.InAssemblies(Assemblies.All).That().AreClasses().And().ImplementInterface(typeof(IDomainEvent));
        domainEvents.GetTypes().ShouldNotBeEmpty();

        var result = domainEvents.Should().BeSealed().And().HaveNameEndingWith("DomainEvent").GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Theory]
    [MemberData(nameof(Assemblies.ModuleNames), MemberType = typeof(Assemblies))]
    public void Module_types_are_internal_except_the_module_entry_point(string moduleName)
    {
        var entryPoints = Types.InAssembly(Assemblies.Module(moduleName)).That().ArePublic().And().HaveNameEndingWith("Module");
        entryPoints.GetTypes().ShouldHaveSingleItem();

        // EF Core generates public migrations and model snapshots; they are the only other public types.
        var publicTypes = Types.InAssembly(Assemblies.Module(moduleName))
            .That()
            .ArePublic()
            .And()
            .DoNotHaveNameEndingWith("Module")
            .And()
            .DoNotInherit(typeof(Migration))
            .And()
            .DoNotInherit(typeof(ModelSnapshot));

        var result = publicTypes.Should().NotBePublic().GetResult();

        result.IsSuccessful.ShouldBeTrue(string.Join(", ", result.FailingTypeNames ?? []));
    }
}
