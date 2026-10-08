using System.Text.RegularExpressions;
using TemplateName.Application.Common.Identity;
using TemplateName.Modules.Auth.Application;

namespace TemplateName.UnitTests.Auth;

public sealed partial class AuthPermissionSourceTests
{
    [GeneratedRegex(@"^[a-z]+(\.[a-z_]+){2}$")]
    private static partial Regex CodePattern();

    [Fact]
    public void AuthPermissionSource_codes_are_unique_snake_case_module_resource_action()
    {
        IPermissionSource source = new AuthPermissionSource();
        var permissions = source.Permissions;

        permissions.Count.ShouldBe(10);
        permissions.Select(p => p.Code).Distinct(StringComparer.Ordinal).Count().ShouldBe(permissions.Count);
        permissions.ShouldAllBe(p => CodePattern().IsMatch(p.Code));
        permissions.ShouldAllBe(p => p.Code.StartsWith("auth.", StringComparison.Ordinal));
        permissions.ShouldAllBe(p => p.Module == "auth");
        permissions.ShouldAllBe(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Description));
    }

    [Fact]
    public void AuthPermissionSource_declares_every_AuthPermissions_constant()
    {
        var declared = typeof(AuthPermissions)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!);

        new AuthPermissionSource().Permissions.Select(p => p.Code).ShouldBe(declared, ignoreOrder: true);
    }
}
