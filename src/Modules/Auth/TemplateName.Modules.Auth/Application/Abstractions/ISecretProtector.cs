using System.Security.Cryptography;

namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Encrypts a secret that has to leave the process, such as a token inside an outbox event (ADR 0017).</summary>
internal interface ISecretProtector
{
    string Protect(string plaintext);

    /// <exception cref="CryptographicException">The value was changed or was not protected by this key ring.</exception>
    string Unprotect(string protectedValue);
}
