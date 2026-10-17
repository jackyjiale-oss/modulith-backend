using TemplateName.Modules.Auth.Domain.Sessions;

namespace TemplateName.UnitTests.Auth;

public sealed class AuthenticationMethodsTests
{
    [Fact]
    public void Join_uses_single_spaces_and_refuses_empty_or_spaced_values()
    {
        AuthenticationMethods.Join(AuthenticationMethods.Password).ShouldBe("pwd");
        AuthenticationMethods.Join(AuthenticationMethods.Password, AuthenticationMethods.OneTimePassword, AuthenticationMethods.MultiFactor)
            .ShouldBe("pwd otp mfa");
        AuthenticationMethods.Join(AuthenticationMethods.Password, AuthenticationMethods.RecoveryCode).ShouldBe("pwd rcvr");

        Should.Throw<ArgumentException>(() => AuthenticationMethods.Join());
        Should.Throw<ArgumentException>(() => AuthenticationMethods.Join("pwd otp"));
        Should.Throw<ArgumentException>(() => AuthenticationMethods.Join("pwd", " "));
        Should.Throw<ArgumentException>(() => AuthenticationMethods.Join("pwd", ""));
    }

    [Fact]
    public void Constants_are_the_rfc_8176_values_the_token_carries()
    {
        AuthenticationMethods.Password.ShouldBe("pwd");
        AuthenticationMethods.OneTimePassword.ShouldBe("otp");
        AuthenticationMethods.RecoveryCode.ShouldBe("rcvr");
        AuthenticationMethods.MultiFactor.ShouldBe("mfa");
    }
}
