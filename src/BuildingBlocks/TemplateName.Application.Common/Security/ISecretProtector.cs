using System.Security.Cryptography;

namespace TemplateName.Application.Common.Security;

/// <summary>
/// Encrypts a secret that has to leave the process or be stored by another module, such as a token inside an outbox event or a
/// link inside an integration event (ADR 0017, ADR 0020).
/// </summary>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <exception cref="CryptographicException">The value was changed or was not protected by this key ring.</exception>
    string Unprotect(string protectedValue);
}
