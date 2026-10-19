using System.Security.Cryptography;
using System.Text;
using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class Base32Tests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Encode_and_decode_match_the_rfc_4648_vectors_without_padding(string plain, string encoded)
    {
        var bytes = Encoding.ASCII.GetBytes(plain);

        Base32.Encode(bytes).ShouldBe(encoded);
        Base32.Decode(encoded).ShouldBe(bytes);
    }

    [Fact]
    public void Twenty_random_bytes_round_trip()
    {
        var secret = RandomNumberGenerator.GetBytes(20);

        var encoded = Base32.Encode(secret);

        encoded.Length.ShouldBe(32);
        encoded.ShouldAllBe(character => (character >= 'A' && character <= 'Z') || (character >= '2' && character <= '7'));
        Base32.Decode(encoded).ShouldBe(secret);
    }

    [Fact]
    public void Decode_accepts_lower_case_and_spaces()
    {
        var expected = Encoding.ASCII.GetBytes("foobar");

        Base32.Decode("mzxw6ytboi").ShouldBe(expected);
        Base32.Decode("MZXW 6YTB OI").ShouldBe(expected);
        Base32.Decode(" mzxw 6ytb oi ").ShouldBe(expected);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("8")]
    [InlineData("=")]
    [InlineData("MZX1")]
    [InlineData("MZX8")]
    [InlineData("MY======")]
    [InlineData("MZ-Q")]
    [InlineData("MZ\tQ")]
    [InlineData("MZÉQ")]
    public void Decode_refuses_characters_outside_the_alphabet(string text)
    {
        Should.Throw<FormatException>(() => Base32.Decode(text));
    }

    [Theory]
    [InlineData("M")]
    [InlineData("MZX")]
    [InlineData("MZXW6Y")]
    public void Decode_refuses_a_length_no_encoding_produces(string text)
    {
        Should.Throw<FormatException>(() => Base32.Decode(text));
    }
}
