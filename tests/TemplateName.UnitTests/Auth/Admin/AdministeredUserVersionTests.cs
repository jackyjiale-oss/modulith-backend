using TemplateName.Modules.Auth.Application.Admin.Users;

namespace TemplateName.UnitTests.Auth.Admin;

public sealed class AdministeredUserVersionTests
{
    private static readonly Guid Support = Guid.Parse("11111111-0000-0000-0000-000000000000");
    private static readonly Guid Auditor = Guid.Parse("22222222-0000-0000-0000-000000000000");

    [Fact]
    public void Covers_the_role_set()
    {
        AdministeredUserVersion.Of([Support]).ShouldNotBe(AdministeredUserVersion.Of([Support, Auditor]));
        AdministeredUserVersion.Of([]).ShouldNotBe(AdministeredUserVersion.Of([Support]));
        AdministeredUserVersion.Of([Support]).ShouldBe(AdministeredUserVersion.Of([Support]));
    }

    [Fact]
    public void Role_order_does_not_matter()
        => AdministeredUserVersion.Of([Auditor, Support]).ShouldBe(AdministeredUserVersion.Of([Support, Auditor]));
}
