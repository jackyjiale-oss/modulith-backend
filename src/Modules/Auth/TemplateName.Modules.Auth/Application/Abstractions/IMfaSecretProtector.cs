using System.Security.Cryptography;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>
/// Encrypts the TOTP secret of an authenticator app for <c>auth.UserMfaMethods.SecretEncrypted</c>. It has a Data Protection purpose of
/// its own, so only Auth can read a secret: the shared <c>ISecretProtector</c>, which other modules use to read what Auth hands them
/// (ADR 0020), cannot unprotect it, and this one cannot unprotect theirs (plan decision D4). Both use the key ring in
/// <c>auth.DataProtectionKeys</c>; losing it makes every stored secret unreadable.
/// </summary>
internal interface IMfaSecretProtector
{
    /// <summary>The protected form of <paramref name="secret"/>, as base64url text; the same secret protected twice differs.</summary>
    string Protect(ReadOnlySpan<byte> secret);

    /// <exception cref="CryptographicException">
    /// The value was changed, is not base64url, was protected under another purpose, or its key is not in the key ring.
    /// </exception>
    byte[] Unprotect(string protectedSecret);
}
