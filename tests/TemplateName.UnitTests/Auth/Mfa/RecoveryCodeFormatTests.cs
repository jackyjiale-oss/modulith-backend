using System.Security.Cryptography;
using System.Text;
using TemplateName.Modules.Auth.Domain.Mfa;

namespace TemplateName.UnitTests.Auth.Mfa;

public sealed class RecoveryCodeFormatTests
{
    [Fact]
    public void Parameters_give_ten_codes_of_eighty_bits()
    {
        RecoveryCodeFormat.Count.ShouldBe(10);
        RecoveryCodeFormat.Length.ShouldBe(16);
        RecoveryCodeFormat.Alphabet.ShouldBe("23456789ABCDEFGHJKLMNPQRSTUVWXYZ");
        RecoveryCodeFormat.Alphabet.Length.ShouldBe(32);
        RecoveryCodeFormat.Alphabet.Distinct().Count().ShouldBe(32);
    }

    [Fact]
    public void Generate_gives_sixteen_alphabet_characters_and_unique_values()
    {
        var codes = Enumerable.Range(0, 1000).Select(_ => RecoveryCodeFormat.Generate()).ToList();

        codes.Distinct(StringComparer.Ordinal).Count().ShouldBe(1000);
        foreach (var code in codes)
        {
            code.Length.ShouldBe(16);
            code.ShouldAllBe(character => RecoveryCodeFormat.Alphabet.Contains(character, StringComparison.Ordinal));
            code.IndexOfAny(['0', '1', 'I', 'O']).ShouldBe(-1);
            RecoveryCodeFormat.Normalize(code).ShouldBe(code);
        }
    }

    [Theory]
    [InlineData("abcd-efgh-jkmn-pqrs")]
    [InlineData("ABCDEFGHJKMNPQRS")]
    [InlineData(" abcd efgh jkmn pqrs \n")]
    [InlineData("ABCD-EFGH-JKMN-PQRS\r\n")]
    [InlineData("AbCd EfGh-JkMn PqRs")]
    public void Normalize_accepts_case_hyphens_and_spaces(string input)
    {
        RecoveryCodeFormat.Normalize(input).ShouldBe("ABCDEFGHJKMNPQRS");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABCDEFGHJKMNPQR")]
    [InlineData("ABCDEFGHJKMNPQRST")]
    [InlineData("ABCD-EFGH-JKMN-PQR")]
    [InlineData("ABCDEFGHJKMNPQRO")]
    [InlineData("ABCDEFGHJKMNPQR1")]
    [InlineData("ABCDEFGHJKMNPQR0")]
    [InlineData("ABCDEFGHJKMNPQRI")]
    [InlineData("ABCD_EFGH_JKMN_PQRS")]
    [InlineData("ABCD\tEFGH\tJKMN\tPQRS")]
    [InlineData("ABCDEFGHJKMNPQRſ")]
    [InlineData("ABCDEFGHJKMNPQRＳ")]
    public void Normalize_refuses_wrong_length_or_characters(string input)
    {
        RecoveryCodeFormat.Normalize(input).ShouldBeNull();
    }

    [Fact]
    public void Display_groups_by_four()
    {
        RecoveryCodeFormat.Display("ABCDEFGHJKMNPQRS").ShouldBe("ABCD-EFGH-JKMN-PQRS");
        RecoveryCodeFormat.Normalize(RecoveryCodeFormat.Display("ABCDEFGHJKMNPQRS")).ShouldBe("ABCDEFGHJKMNPQRS");

        Should.Throw<ArgumentException>(() => RecoveryCodeFormat.Display("ABCD-EFGH-JKMN-PQRS"));
        Should.Throw<ArgumentException>(() => RecoveryCodeFormat.Display("abcdefghjkmnpqrs"));
    }

    [Fact]
    public void Hash_is_sha256_of_the_normalized_code()
    {
        var hash = RecoveryCodeFormat.Hash("ABCDEFGHJKMNPQRS");

        hash.Length.ShouldBe(32);
        hash.ShouldBe(SHA256.HashData(Encoding.UTF8.GetBytes("ABCDEFGHJKMNPQRS")));
        RecoveryCodeFormat.Hash(RecoveryCodeFormat.Normalize(" abcd-efgh jkmn-pqrs ")!).ShouldBe(hash);
        RecoveryCodeFormat.Hash("ABCDEFGHJKMNPQRT").SequenceEqual(hash).ShouldBeFalse();

        Should.Throw<ArgumentException>(() => RecoveryCodeFormat.Hash("abcd-efgh-jkmn-pqrs"));
    }
}
