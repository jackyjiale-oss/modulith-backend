using System.Net;
using TemplateName.Modules.Auth.Application.Verification;

namespace TemplateName.UnitTests.Auth;

public sealed class AuthEmailsTests
{
    private const string HostileName = "<script>alert(\"x\")</script> & O'Brien";
    private const string Address = "alice@example.com";

    [Fact]
    public void ConfirmEmail_contains_link_with_encoded_token_and_no_html_injection_from_display_name()
    {
        var link = new LinksOptions().ConfirmEmailLink("a+b/c=d&e");

        var message = AuthEmails.ConfirmEmail(Address, HostileName, link);

        message.To.ShouldBe(Address);
        link.ShouldContain("token=a%2Bb%2Fc%3Dd%26e");
        message.TextBody.ShouldContain(link);
        message.TextBody.ShouldNotContain("<html");
        message.TextBody.ShouldNotContain("<p>");
        message.HtmlBody.ShouldNotBeNull();
        message.HtmlBody.ShouldContain($"href=\"{link}\"");
        message.HtmlBody.ShouldNotContain("<script");
        message.HtmlBody.ShouldNotContain(HostileName);
        message.HtmlBody.ShouldContain(WebUtility.HtmlEncode(HostileName));
    }

    [Fact]
    public void Link_with_an_ampersand_is_attribute_encoded_in_html_and_left_alone_in_text()
    {
        const string Link = "https://app.example.com/reset?lang=en&token=abc";

        var message = AuthEmails.ResetPassword(Address, "Alice", Link);

        message.TextBody.ShouldContain(Link);
        message.HtmlBody.ShouldNotBeNull();
        message.HtmlBody.ShouldContain("href=\"https://app.example.com/reset?lang=en&amp;token=abc\"");
        message.HtmlBody.ShouldNotContain("lang=en&token");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/path?token=abc")]
    [InlineData("")]
    public void Builders_refuse_a_link_that_is_not_an_absolute_http_address(string link)
    {
        Should.Throw<ArgumentException>(() => AuthEmails.ConfirmEmail(Address, "Alice", link));
        Should.Throw<ArgumentException>(() => AuthEmails.ResetPassword(Address, "Alice", link));
    }

    [Fact]
    public void Display_name_never_reaches_the_subject()
    {
        var message = AuthEmails.RegistrationAttempted(Address, "Alice\r\nBcc: attacker@example.com");

        message.Subject.ShouldNotContain("Alice");
        message.Subject.ShouldNotContain("\n");
    }

    [Fact]
    public void All_builders_produce_text_and_html_bodies()
    {
        const string Link = "https://app.example.com/go?token=abc";
        var messages = new[]
        {
            AuthEmails.ConfirmEmail(Address, "Alice", Link),
            AuthEmails.ResetPassword(Address, "Alice", Link),
            AuthEmails.RegistrationAttempted(Address, "Alice"),
        };

        foreach (var message in messages)
        {
            message.To.ShouldBe(Address);
            message.Subject.ShouldNotBeNullOrWhiteSpace();
            message.TextBody.ShouldNotBeNullOrWhiteSpace();
            message.TextBody.ShouldNotContain("<");
            message.HtmlBody.ShouldNotBeNull();
            message.HtmlBody.ShouldContain("<html");
            message.HtmlBody.ShouldContain("Alice");
        }

        messages.Select(message => message.Subject).Distinct().Count().ShouldBe(messages.Length);
        messages[2].TextBody.ShouldNotContain("http");
    }
}
