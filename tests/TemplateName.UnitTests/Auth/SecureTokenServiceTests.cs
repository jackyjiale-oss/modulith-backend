using System.Security.Cryptography;
using System.Text;
using TemplateName.Modules.Auth.Infrastructure.Security;

namespace TemplateName.UnitTests.Auth;

public sealed class SecureTokenServiceTests
{
    private readonly SecureTokenService _service = new();

    [Fact]
    public void Generated_token_is_43_base64url_characters_and_unique()
    {
        var tokens = Enumerable.Range(0, 100).Select(_ => _service.Generate().Value).ToList();

        tokens.ShouldAllBe(token => token.Length == 43);
        tokens.ShouldAllBe(token => token.All(character => char.IsAsciiLetterOrDigit(character) || character == '-' || character == '_'));
        tokens.Distinct().Count().ShouldBe(tokens.Count);
    }

    [Fact]
    public void Hash_is_deterministic_32_bytes_and_matches_Generate()
    {
        var generated = _service.Generate();

        var hash = _service.Hash(generated.Value);

        hash.Length.ShouldBe(32);
        hash.ShouldBe(generated.Hash);
        hash.ShouldBe(_service.Hash(generated.Value));
        hash.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes(generated.Value)));
        _service.Hash("another token").ShouldNotBe(hash);
    }
}
