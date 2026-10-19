using TemplateName.Modules.Notifications.Application.Admin;

namespace TemplateName.UnitTests.Notifications;

public sealed class DestinationMaskTests
{
    [Fact]
    public void Mask_keeps_the_first_character_and_the_domain_of_an_address()
    {
        DestinationMask.Mask("alice@example.com").ShouldBe("a****@example.com");
        DestinationMask.Mask("Bob.Smith+tag@Mail.Example.org").ShouldBe("B****@Mail.Example.org");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Mask_of_no_destination_is_null(string? destination)
    {
        DestinationMask.Mask(destination).ShouldBeNull();
    }

    [Theory]
    [InlineData("a@example.com", "****@example.com")]
    [InlineData("ab@example.com", "****@example.com")]
    [InlineData("abc@example.com", "a****@example.com")]
    public void Mask_shows_the_first_character_only_when_the_local_part_has_at_least_three(string address, string expected)
    {
        // A one-character local part would be revealed whole, a two-character one by half.
        DestinationMask.Mask(address).ShouldBe(expected);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("alice")]
    [InlineData("alice@")]
    [InlineData("@")]
    public void Mask_without_a_local_part_or_a_domain_hides_everything(string destination)
    {
        DestinationMask.Mask(destination).ShouldBe("****");
    }

    [Fact]
    public void Mask_of_an_empty_local_part_hides_everything()
    {
        // Nothing before the at sign, so it is not an address a person could have: no part of it is shown.
        DestinationMask.Mask("@example.com").ShouldBe("****");
    }

    [Fact]
    public void Mask_of_a_very_long_address_has_a_fixed_length_local_part()
    {
        var local = new string('x', 64);

        var masked = DestinationMask.Mask($"{local}@example.com");

        masked.ShouldBe("x****@example.com");
        masked.ShouldNotBeNull().ShouldNotContain("xx");
    }

    [Fact]
    public void Mask_counts_letters_not_utf16_units()
    {
        // The first letter is kept whole, whether it is accented, in another script, a surrogate pair or a combining sequence.
        DestinationMask.Mask("ülrich@exämple.com").ShouldBe("ü****@exämple.com");
        DestinationMask.Mask("张伟明@example.cn").ShouldBe("张****@example.cn");
        DestinationMask.Mask("\U0001F600abc@example.com").ShouldBe("\U0001F600****@example.com");
        DestinationMask.Mask("éabc@example.com").ShouldBe("é****@example.com");

        // Two emoji are two letters, so too short to show one; a lone combining sequence is one.
        DestinationMask.Mask("\U0001F600\U0001F600@example.com").ShouldBe("****@example.com");
        DestinationMask.Mask("é@example.com").ShouldBe("****@example.com");
    }

    [Fact]
    public void Mask_splits_at_the_last_at_sign()
    {
        // A quoted local part may hold an @; the domain is what follows the last one, and nothing before it is shown but the first letter.
        DestinationMask.Mask("a@b@example.com").ShouldBe("a****@example.com");
        DestinationMask.Mask("\"secret@part\"@example.com").ShouldBe("\"****@example.com");
        DestinationMask.Mask("a@@example.com").ShouldBe("****@example.com");
    }

    [Fact]
    public void Mask_never_returns_more_of_the_local_part_than_its_first_letter()
    {
        foreach (var address in new[] { "alice@example.com", "bob.the.builder@example.com", "zhang.wei@example.cn" })
        {
            var masked = DestinationMask.Mask(address).ShouldNotBeNull();
            var local = address[..address.LastIndexOf('@')];
            masked.Contains(local[1..], StringComparison.Ordinal).ShouldBeFalse(address);
        }
    }
}
