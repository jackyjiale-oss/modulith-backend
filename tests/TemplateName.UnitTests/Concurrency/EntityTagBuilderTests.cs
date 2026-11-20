using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using TemplateName.Application.Common.Concurrency;

namespace TemplateName.UnitTests.Concurrency;

public sealed class EntityTagBuilderTests
{
    private static readonly Guid GuidA = Guid.Parse("0a000000-0000-0000-0000-000000000000");
    private static readonly Guid GuidB = Guid.Parse("0b000000-0000-0000-0000-000000000000");

    [Fact]
    public void Canonical_text_has_the_format_version_kind_and_tokens()
        => new EntityTagBuilder("role").Add("ab").Add((string?)null).AddSet(["b", "a"]).CanonicalText
            .ShouldBe("v1;s4:role;s2:ab;n;l2:s1:a;s1:b;");

    [Fact]
    public void Guids_are_lower_case_d_strings_and_sets_of_them_are_sorted()
        => new EntityTagBuilder("k").Add(GuidB).AddSet([GuidB, GuidA]).CanonicalText
            .ShouldBe("v1;s1:k;s36:0b000000-0000-0000-0000-000000000000;l2:s36:0a000000-0000-0000-0000-000000000000;s36:0b000000-0000-0000-0000-000000000000;");

    [Fact]
    public void Bools_and_times_are_invariant_string_tokens()
        => new EntityTagBuilder("k").Add(true).Add(new TimeOnly(22, 0)).Add((TimeOnly?)null).CanonicalText
            .ShouldBe("v1;s1:k;s4:true;s16:22:00:00.0000000;n;");

    [Fact]
    public void Length_prefixes_keep_split_strings_apart()
        => new EntityTagBuilder("k").Add("ab").Add("c").Build().ShouldNotBe(new EntityTagBuilder("k").Add("a").Add("bc").Build());

    [Fact]
    public void Null_and_empty_differ()
        => new EntityTagBuilder("k").Add((string?)null).Build().ShouldNotBe(new EntityTagBuilder("k").Add(string.Empty).Build());

    [Fact]
    public void Set_order_does_not_matter()
        => new EntityTagBuilder("k").AddSet([GuidB, GuidA]).Build().ShouldBe(new EntityTagBuilder("k").AddSet([GuidA, GuidB]).Build());

    [Fact]
    public void A_different_kind_gives_a_different_tag()
        => new EntityTagBuilder("role").Add("x").Build().ShouldNotBe(new EntityTagBuilder("profile").Add("x").Build());

    [Fact]
    public void Strings_are_taken_as_stored()
        => new EntityTagBuilder("k").Add("A ").Build().ShouldNotBe(new EntityTagBuilder("k").Add("a").Build());

    // Test data is built here, not in [InlineData]: xUnit's case serialization cannot carry a lone surrogate.
    [Fact]
    public void A_lone_surrogate_is_not_replaced_before_hashing()
    {
        string[] values = ["a\uD800", "a\uDC00", "a�", "\uDC00\uD800", "��"];

        values.Select(value => new EntityTagBuilder("k").Add(value).Build()).Distinct().Count().ShouldBe(values.Length);
    }

    [Fact]
    public void Build_is_the_first_16_bytes_of_sha256_in_base64url()
    {
        var builder = new EntityTagBuilder("k").Add("x");
        var expected = Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.CanonicalText))[..16]);

        builder.Build().Value.ShouldBe(expected);
        builder.Build().ToHeaderValue().ShouldBe($"\"{expected}\"");
    }
}
