namespace TemplateName.Modules.Auth.Application.Abstractions;

/// <summary>Makes and hashes the bearer secrets the module stores only as hashes (refresh and verification tokens).</summary>
internal interface ISecureTokenService
{
    /// <summary>Makes a token from 32 random bytes (43 base64url characters) together with its hash.</summary>
    GeneratedToken Generate();

    /// <summary>The SHA-256 of the UTF-8 bytes of a token, the form that is stored and looked up.</summary>
    byte[] Hash(string token);
}
