using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using TemplateName.Modules.Auth.Infrastructure.Security;

namespace TemplateName.UnitTests.Auth;

public sealed class SecretProtectorTests
{
    private const string Secret = "the-verification-token";

    private readonly DataProtectionSecretProtector _protector = new(new EphemeralDataProtectionProvider());

    [Fact]
    public void Round_trips()
    {
        var protectedValue = _protector.Protect(Secret);

        protectedValue.ShouldNotContain(Secret);
        _protector.Unprotect(protectedValue).ShouldBe(Secret);
    }

    [Fact]
    public void Tampered_value_throws_CryptographicException()
    {
        var protectedValue = _protector.Protect(Secret);
        var middle = protectedValue.Length / 2;
        var replacement = protectedValue[middle] == 'A' ? 'B' : 'A';
        var tampered = string.Concat(protectedValue.AsSpan(0, middle), replacement.ToString(), protectedValue.AsSpan(middle + 1));

        Should.Throw<CryptographicException>(() => _protector.Unprotect(tampered));
    }

    [Fact]
    public void Protected_value_differs_between_calls()
    {
        _protector.Protect(Secret).ShouldNotBe(_protector.Protect(Secret));
    }
}
