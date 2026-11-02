using TemplateName.Modules.Auth.Domain.Sessions;

namespace TemplateName.UnitTests.Auth;

public sealed class SessionRevokedReasonTests
{
    [Fact]
    public void ExternalLoginChanged_is_7_and_the_others_keep_their_values()
    {
        ((int)SessionRevokedReason.Logout).ShouldBe(0);
        ((int)SessionRevokedReason.LogoutAll).ShouldBe(1);
        ((int)SessionRevokedReason.PasswordChanged).ShouldBe(2);
        ((int)SessionRevokedReason.AdminRevoked).ShouldBe(3);
        ((int)SessionRevokedReason.TokenReuse).ShouldBe(4);
        ((int)SessionRevokedReason.Expired).ShouldBe(5);
        ((int)SessionRevokedReason.MfaChanged).ShouldBe(6);
        ((int)SessionRevokedReason.ExternalLoginChanged).ShouldBe(7);
    }
}
