using System.Text;
using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class TotpUriTests
{
    [Fact]
    public void Build_escapes_issuer_and_account_and_carries_every_parameter()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");

        var uri = TotpUri.Build("Acme Hr", "alice+mfa@example.com", secret);

        uri.ShouldBe(
            "otpauth://totp/Acme%20Hr:alice%2Bmfa%40example.com"
            + "?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ"
            + "&issuer=Acme%20Hr&algorithm=SHA1&digits=6&period=30");
    }

    [Fact]
    public void Build_escapes_a_colon_so_the_label_keeps_one_separator()
    {
        var uri = TotpUri.Build("Acme: Hr", "alice:bob@example.com", [1, 2, 3, 4, 5]);

        uri.ShouldStartWith("otpauth://totp/Acme%3A%20Hr:alice%3Abob%40example.com?secret=AEBAGBAF&issuer=Acme%3A%20Hr&");
    }

    [Fact]
    public void Build_refuses_an_empty_issuer_account_or_secret()
    {
        byte[] secret = [1, 2, 3, 4, 5];

        Should.Throw<ArgumentException>(() => TotpUri.Build("", "alice@example.com", secret));
        Should.Throw<ArgumentException>(() => TotpUri.Build(" ", "alice@example.com", secret));
        Should.Throw<ArgumentException>(() => TotpUri.Build("Acme", "", secret));
        Should.Throw<ArgumentException>(() => TotpUri.Build("Acme", "alice@example.com", []));
    }
}
