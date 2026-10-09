using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using TemplateName.Infrastructure.Common.Security;

namespace TemplateName.UnitTests.Infrastructure;

public sealed class SecretProtectorTests
{
    private const string Secret = "the-verification-token";

    private readonly EphemeralDataProtectionProvider _provider = new();
    private readonly DataProtectionSecretProtector _protector;

    public SecretProtectorTests() => _protector = new DataProtectionSecretProtector(_provider);

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

    [Fact]
    public void Purpose_is_the_shared_secrets_purpose_v1()
    {
        var protectedValue = _provider.CreateProtector("TemplateName.Secrets.v1").Protect(Secret);

        _protector.Unprotect(protectedValue).ShouldBe(Secret);
    }
}
