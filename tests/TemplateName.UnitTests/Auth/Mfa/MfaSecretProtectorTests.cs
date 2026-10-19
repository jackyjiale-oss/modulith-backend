using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using TemplateName.Infrastructure.Common.Security;
using TemplateName.Modules.Auth.Domain.Mfa;
using TemplateName.Modules.Auth.Infrastructure.Security;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class MfaSecretProtectorTests
{
    private const string Purpose = "TemplateName.Auth.MfaSecrets.v1";

    private readonly EphemeralDataProtectionProvider _provider = new();
    private readonly DataProtectionMfaSecretProtector _protector;

    public MfaSecretProtectorTests() => _protector = new DataProtectionMfaSecretProtector(_provider);

    [Fact]
    public void Protector_round_trips_and_refuses_tampering()
    {
        var secret = Totp.NewSecret();

        var protectedSecret = _protector.Protect(secret);

        // Fits the column, and neither the secret's base32 (what the user sees) nor its hex or base64 form is in the stored text.
        protectedSecret.Length.ShouldBeLessThanOrEqualTo(UserMfaMethod.MaxSecretEncryptedLength);
        protectedSecret.ShouldNotContain(Base32.Encode(secret));
        protectedSecret.ShouldNotContain(Convert.ToHexString(secret), Case.Insensitive);
        protectedSecret.ShouldNotContain(Convert.ToBase64String(secret).TrimEnd('='));
        _protector.Unprotect(protectedSecret).ShouldBe(secret);
        _protector.Protect(secret).ShouldNotBe(protectedSecret);

        var middle = protectedSecret.Length / 2;
        var replacement = protectedSecret[middle] == 'A' ? 'B' : 'A';
        var tampered = string.Concat(protectedSecret.AsSpan(0, middle), replacement.ToString(), protectedSecret.AsSpan(middle + 1));
        Should.Throw<CryptographicException>(() => _protector.Unprotect(tampered));

        // Text that is not base64url at all, or nothing, is refused the same way: never a FormatException.
        Should.Throw<CryptographicException>(() => _protector.Unprotect("not base64url!"));
        Should.Throw<CryptographicException>(() => _protector.Unprotect(protectedSecret + "="));
        Should.Throw<CryptographicException>(() => _protector.Unprotect(string.Empty));
    }

    [Fact]
    public void Purpose_is_isolated_from_the_shared_secret_protector()
    {
        var shared = new DataProtectionSecretProtector(_provider);
        var secret = Totp.NewSecret();

        // The shared purpose (TemplateName.Secrets.v1, readable by other modules) and this one cannot read each other's values,
        // although both use the same key ring and the same base64url text form.
        var sharedValue = shared.Protect(Base32.Encode(secret));
        Should.Throw<CryptographicException>(() => _protector.Unprotect(sharedValue));
        Should.Throw<CryptographicException>(() => shared.Unprotect(_protector.Protect(secret)));

        // The purpose is exactly the dedicated one: a value protected under it by hand reads back.
        var byHand = Base64Url.EncodeToString(_provider.CreateProtector(Purpose).Protect(secret));
        _protector.Unprotect(byHand).ShouldBe(secret);
    }
}
