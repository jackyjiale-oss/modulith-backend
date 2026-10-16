using System.ComponentModel.DataAnnotations;
using TemplateName.Modules.Auth.Application.Verification;

namespace TemplateName.UnitTests.Auth;

public sealed class LinksOptionsTests
{
    [Fact]
    public void Defaults_are_valid_and_carry_the_token_placeholder()
    {
        var links = new LinksOptions();

        Validate(links).ShouldBeEmpty();
        LinksOptions.SectionName.ShouldBe("Auth:Links");
        links.ConfirmEmailUrl.ShouldBe("http://localhost:3000/confirm-email?token={token}");
        links.ResetPasswordUrl.ShouldBe("http://localhost:3000/reset-password?token={token}");
    }

    [Fact]
    public void Links_url_encode_the_token_when_substituting()
    {
        var links = new LinksOptions();

        links.ConfirmEmailLink("a-b_c").ShouldBe("http://localhost:3000/confirm-email?token=a-b_c");
        links.ResetPasswordLink("x y&z=1/2+3").ShouldBe("http://localhost:3000/reset-password?token=x%20y%26z%3D1%2F2%2B3");
    }

    [Theory]
    [InlineData("http://localhost:3000/confirm-email")]
    [InlineData("/confirm-email?token={token}")]
    [InlineData("javascript:alert({token})")]
    [InlineData("")]
    public void Links_without_a_placeholder_or_an_absolute_http_address_are_rejected(string url)
    {
        Validate(new LinksOptions { ConfirmEmailUrl = url }).ShouldNotBeEmpty();
        Validate(new LinksOptions { ResetPasswordUrl = url }).ShouldNotBeEmpty();
    }

    private static List<ValidationResult> Validate(LinksOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
