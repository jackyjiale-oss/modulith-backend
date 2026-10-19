using System.Buffers.Text;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Infrastructure.Security;

/// <summary>
/// Protects TOTP secrets with ASP.NET Core Data Protection under a purpose of their own (plan decision D4), on the key ring every
/// instance shares (ADR 0017). The text form is base64url without padding, like the shared protector's; a 20-byte secret becomes about
/// 155 characters with the default algorithms (the column holds 512).
/// </summary>
internal sealed class DataProtectionMfaSecretProtector(IDataProtectionProvider provider) : IMfaSecretProtector
{
    /// <summary>Changing it makes every stored secret unreadable, which turns off every authenticator app.</summary>
    private const string Purpose = "TemplateName.Auth.MfaSecrets.v1";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(ReadOnlySpan<byte> secret)
    {
        var plaintext = secret.ToArray();
        try
        {
            return Base64Url.EncodeToString(_protector.Protect(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public byte[] Unprotect(string protectedSecret)
    {
        ArgumentNullException.ThrowIfNull(protectedSecret);

        // The text form has no padding, and the decoder's tolerance of it is not the same on every platform: refuse it here.
        if (protectedSecret.Contains('='))
        {
            throw new CryptographicException("The protected secret is not valid base64url.");
        }

        byte[] payload;
        try
        {
            payload = Base64Url.DecodeFromChars(protectedSecret);
        }
        catch (FormatException exception)
        {
            // As the Data Protection string API does: text that is not base64url is a value this protector did not make.
            throw new CryptographicException("The protected secret is not valid base64url.", exception);
        }

        return _protector.Unprotect(payload);
    }
}
