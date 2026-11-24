using TemplateName.Modules.Auth.Domain.Verification;

namespace TemplateName.UnitTests.Auth.Phone;

public sealed class PhoneNumberRulesTests
{
    private static readonly IReadOnlySet<string> AnyRegion = new HashSet<string>(StringComparer.Ordinal);

    [Fact]
    public void A_malaysian_mobile_is_normalized()
    {
        PhoneNumberRules.TryNormalize("+60123456789", AnyRegion, out var e164).ShouldBeTrue();

        e164.ShouldBe("+60123456789");
    }

    [Theory]
    [InlineData("+60 12-345 6789")]
    [InlineData("+60 (12) 345-6789")]
    [InlineData(" +60123456789 ")]
    public void Formatting_is_normalized_to_e164(string input)
    {
        PhoneNumberRules.TryNormalize(input, AnyRegion, out var e164).ShouldBeTrue();

        e164.ShouldBe("+60123456789");
    }

    [Fact]
    public void A_number_without_plus_is_refused()
    {
        PhoneNumberRules.TryNormalize("0123456789", AnyRegion, out var e164).ShouldBeFalse();

        e164.ShouldBeEmpty();
    }

    [Fact]
    public void A_landline_is_refused() =>
        PhoneNumberRules.TryNormalize("+60321234567", AnyRegion, out _).ShouldBeFalse();

    [Fact]
    public void A_region_outside_the_allowlist_is_refused()
    {
        var malaysiaOnly = new HashSet<string>(StringComparer.Ordinal) { "MY" };

        PhoneNumberRules.TryNormalize("+6581234567", malaysiaOnly, out _).ShouldBeFalse();
        PhoneNumberRules.TryNormalize("+60123456789", malaysiaOnly, out _).ShouldBeTrue();
    }

    [Fact]
    public void An_empty_allowlist_accepts_every_region() =>
        PhoneNumberRules.TryNormalize("+6581234567", AnyRegion, out _).ShouldBeTrue();

    [Theory]
    [InlineData("+abc")]
    [InlineData("+1234567890123456789012345678901234567890")]
    [InlineData("")]
    [InlineData("   ")]
    public void Garbage_is_refused(string input) =>
        PhoneNumberRules.TryNormalize(input, AnyRegion, out _).ShouldBeFalse();

    [Theory]
    [InlineData("MY", true)]
    [InlineData("SG", true)]
    [InlineData("ZZ", false)]
    [InlineData("my", false)]
    public void IsKnownRegion_accepts_only_upper_case_iso_codes(string region, bool expected) =>
        PhoneNumberRules.IsKnownRegion(region).ShouldBe(expected);
}
