using System.ComponentModel.DataAnnotations;
using TemplateName.Modules.Auth.Application.Verification;
using TemplateName.Modules.Auth.Infrastructure.Email;

namespace TemplateName.UnitTests.Auth;

public sealed class EmailOptionsTests
{
    [Fact]
    public void Defaults_are_valid_and_local()
    {
        var options = new EmailOptions();

        Validate(options).ShouldBeEmpty();
        options.Host.ShouldBe("localhost");
        options.Port.ShouldBe(1025);
        options.UseTls.ShouldBeFalse();
        options.Username.ShouldBeEmpty();
        options.Password.ShouldBeEmpty();
        options.From.ShouldBe("no-reply@localhost.test");
        options.FromName.ShouldBe("TemplateName");
        EmailOptions.SectionName.ShouldBe("Auth:Email");
    }

    [Theory]
    [InlineData("", 1025, "no-reply@localhost.test")]
    [InlineData("localhost", 0, "no-reply@localhost.test")]
    [InlineData("localhost", 70000, "no-reply@localhost.test")]
    [InlineData("localhost", 1025, "not-an-address")]
    public void Out_of_range_values_are_rejected(string host, int port, string from)
    {
        var options = new EmailOptions { Host = host, Port = port, From = from };

        Validate(options).ShouldNotBeEmpty();
    }

    [Fact]
    public void Links_defaults_are_valid_and_carry_the_token_placeholder()
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

    private static List<ValidationResult> Validate(object options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
