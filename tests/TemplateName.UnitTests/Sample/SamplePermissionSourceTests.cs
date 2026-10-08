using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Sample;
using TemplateName.Modules.Sample.Application;

namespace TemplateName.UnitTests.Sample;

public sealed class SamplePermissionSourceTests
{
    [Fact]
    public void SamplePermissionSource_declares_the_three_leave_request_permissions()
    {
        IPermissionSource source = new SamplePermissionSource();

        source.Permissions.Select(permission => permission.Code).ShouldBe(
            ["sample.leave_request.view", "sample.leave_request.create", "sample.leave_request.approve"],
            ignoreOrder: true);
        source.Permissions.ShouldAllBe(permission => permission.Module == "sample");
        source.Permissions.ShouldAllBe(permission =>
            !string.IsNullOrWhiteSpace(permission.Name) && !string.IsNullOrWhiteSpace(permission.Description));
    }

    [Fact]
    public void SamplePermissionSource_declares_every_SamplePermissions_constant()
    {
        var declared = typeof(SamplePermissions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!);

        new SamplePermissionSource().Permissions.Select(permission => permission.Code).ShouldBe(declared, ignoreOrder: true);
    }

    [Fact]
    public void AddSampleModule_registers_the_permission_source_as_a_singleton()
    {
        var services = new ServiceCollection();

        services.AddSampleModule();

        services.Where(descriptor => descriptor.ServiceType == typeof(IPermissionSource))
            .ShouldHaveSingleItem()
            .ShouldSatisfyAllConditions(
                descriptor => descriptor.ImplementationType.ShouldBe(typeof(SamplePermissionSource)),
                descriptor => descriptor.Lifetime.ShouldBe(ServiceLifetime.Singleton));
    }
}
