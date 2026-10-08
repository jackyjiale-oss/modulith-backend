using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Infrastructure.Security;

internal sealed class SecureTokenService : ISecureTokenService
{
    private const int TokenByteLength = 32;

    public GeneratedToken Generate()
    {
        var value = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenByteLength));
        return new GeneratedToken(value, Hash(value));
    }

    public byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
