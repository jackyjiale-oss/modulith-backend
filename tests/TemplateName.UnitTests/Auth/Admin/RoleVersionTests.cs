using TemplateName.Modules.Auth.Application.Admin.Roles;

namespace TemplateName.UnitTests.Auth.Admin;

public sealed class RoleVersionTests
{
    private static readonly Guid View = Guid.Parse("11111111-0000-0000-0000-000000000000");
    private static readonly Guid Manage = Guid.Parse("22222222-0000-0000-0000-000000000000");

    [Fact]
    public void Covers_name_description_and_permissions()
    {
        var baseline = RoleVersion.Of("Support", "Tickets", [View]);

        RoleVersion.Of("Helpdesk", "Tickets", [View]).ShouldNotBe(baseline);
        RoleVersion.Of("Support", "Calls", [View]).ShouldNotBe(baseline);
        RoleVersion.Of("Support", "Tickets", [View, Manage]).ShouldNotBe(baseline);
        RoleVersion.Of("Support", "Tickets", [View]).ShouldBe(baseline);
    }

    [Fact]
    public void Permission_order_does_not_matter()
        => RoleVersion.Of("Support", "Tickets", [Manage, View]).ShouldBe(RoleVersion.Of("Support", "Tickets", [View, Manage]));
}
