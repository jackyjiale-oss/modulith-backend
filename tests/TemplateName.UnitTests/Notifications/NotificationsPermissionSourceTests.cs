using System.Reflection;
using System.Text.RegularExpressions;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application;
using TemplateName.Modules.Auth.Infrastructure.Authorization;
using TemplateName.Modules.Notifications.Application;
using TemplateName.Modules.Sample.Application;

namespace TemplateName.UnitTests.Notifications;

public sealed partial class NotificationsPermissionSourceTests
{
    [GeneratedRegex(@"^[a-z]+(\.[a-z_]+){2}\z")]
    private static partial Regex CodePattern();

    [Fact]
    public void Codes_are_unique_module_resource_action()
    {
        IPermissionSource source = new NotificationsPermissionSource();
        var permissions = source.Permissions;

        permissions.Select(permission => permission.Code).ShouldBe(["notifications.delivery.view", "notifications.delivery.retry"], ignoreOrder: true);
        permissions.Select(permission => permission.Code).Distinct(StringComparer.Ordinal).Count().ShouldBe(permissions.Count);
        permissions.ShouldAllBe(permission => CodePattern().IsMatch(permission.Code));
        permissions.ShouldAllBe(permission => permission.Module == "notifications");
        permissions.ShouldAllBe(permission => permission.Code.StartsWith(permission.Module + ".", StringComparison.Ordinal));
        permissions.ShouldAllBe(permission => !string.IsNullOrWhiteSpace(permission.Name) && !string.IsNullOrWhiteSpace(permission.Description));
    }

    [Fact]
    public void Both_permissions_are_admin_defaults()
    {
        new NotificationsPermissionSource().Permissions.ShouldAllBe(permission => permission.AdminDefault);
    }

    [Fact]
    public void Source_declares_every_NotificationsPermissions_constant()
    {
        var declared = typeof(NotificationsPermissions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!);

        new NotificationsPermissionSource().Permissions.Select(permission => permission.Code).ShouldBe(declared, ignoreOrder: true);
    }

    [Fact]
    public void The_auth_synchronizer_accepts_the_source_next_to_the_other_modules()
    {
        // The seeder validates every registered source together: pattern, module = first segment, no code declared twice.
        var definitions = PermissionSynchronizer.Validate(
            [new AuthPermissionSource(), new SamplePermissionSource(), new NotificationsPermissionSource()]);

        definitions.Count.ShouldBe(16);
        definitions.Count(definition => definition.Module == "notifications").ShouldBe(2);
    }

    [Fact]
    public void PermissionDefinition_is_not_an_admin_default_unless_flagged()
    {
        new PermissionDefinition("test.widget.view", "test", "View widgets", "Read widgets.").AdminDefault.ShouldBeFalse();
        new PermissionDefinition("test.widget.view", "test", "View widgets", "Read widgets.", AdminDefault: true).AdminDefault.ShouldBeTrue();
    }
}
