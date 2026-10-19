using System.ComponentModel.DataAnnotations;
using TemplateName.Modules.Notifications.Infrastructure.Channels.Email;

namespace TemplateName.UnitTests.Notifications;

public sealed class EmailOptionsTests
{
    [Fact]
    public void Defaults_are_valid_and_local()
    {
        var options = new EmailOptions();

        Validate(options).ShouldBeEmpty();
        EmailOptions.SectionName.ShouldBe("Notifications:Email");
        options.Host.ShouldBe("localhost");
        options.Port.ShouldBe(1025);
        options.UseTls.ShouldBeFalse();
        options.Username.ShouldBeEmpty();
        options.Password.ShouldBeEmpty();
        options.From.ShouldBe("no-reply@localhost.test");
        options.FromName.ShouldBe("TemplateName");
        options.Timeout.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(1, "00:00:01")]
    [InlineData(65535, "00:05:00")]
    public void Range_limits_are_accepted(int port, string timeout)
    {
        var options = new EmailOptions { Port = port, Timeout = TimeSpan.Parse(timeout, System.Globalization.CultureInfo.InvariantCulture) };

        Validate(options).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("", 1025, "no-reply@localhost.test", "TemplateName", "00:00:30")]
    [InlineData("localhost", 0, "no-reply@localhost.test", "TemplateName", "00:00:30")]
    [InlineData("localhost", 65536, "no-reply@localhost.test", "TemplateName", "00:00:30")]
    [InlineData("localhost", 1025, "not-an-address", "TemplateName", "00:00:30")]
    [InlineData("localhost", 1025, "no-reply@localhost.test", "", "00:00:30")]
    [InlineData("localhost", 1025, "no-reply@localhost.test", "TemplateName", "00:00:00.999")]
    [InlineData("localhost", 1025, "no-reply@localhost.test", "TemplateName", "00:05:01")]
    public void Out_of_range_values_are_rejected(string host, int port, string from, string fromName, string timeout)
    {
        var options = new EmailOptions
        {
            Host = host,
            Port = port,
            From = from,
            FromName = fromName,
            Timeout = TimeSpan.Parse(timeout, System.Globalization.CultureInfo.InvariantCulture),
        };

        Validate(options).ShouldNotBeEmpty();
    }

    [Fact]
    public void From_name_is_at_most_100_characters()
    {
        Validate(new EmailOptions { FromName = new string('x', 100) }).ShouldBeEmpty();
        Validate(new EmailOptions { FromName = new string('x', 101) }).ShouldNotBeEmpty();
    }

    [Fact]
    public void Username_without_tls_is_refused_for_a_remote_host_without_echoing_a_value()
    {
        var options = new EmailOptions { Host = "smtp.mail-provider.test", Username = "mailer-account", Password = "pa55-value-91" };

        var error = Validate(options).ShouldHaveSingleItem();

        error.ErrorMessage.ShouldNotBeNull().ShouldContain("Notifications:Email:UseTls");
        error.ErrorMessage.ShouldContain("Notifications:Email:Username");
        error.ErrorMessage.ShouldNotContain("smtp.mail-provider.test");
        error.ErrorMessage.ShouldNotContain("mailer-account");
        error.ErrorMessage.ShouldNotContain("pa55-value-91");
        error.MemberNames.ShouldBe([nameof(EmailOptions.UseTls)]);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("LocalHost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void Username_without_tls_is_allowed_on_a_loopback_host(string host)
    {
        Validate(new EmailOptions { Host = host, Username = "mailer-account", Password = "pa55-value-91" }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("smtp.mail-provider.test", "mailer-account", true)]
    [InlineData("smtp.mail-provider.test", "", false)]
    [InlineData("10.0.0.5", "", false)]
    public void Tls_or_no_username_is_allowed_for_a_remote_host(string host, string username, bool useTls)
    {
        Validate(new EmailOptions { Host = host, Username = username, UseTls = useTls }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("localhost.mail-provider.test")]
    [InlineData("127.0.0.1.mail-provider.test")]
    public void Username_without_tls_is_refused_for_a_host_that_only_looks_local(string host)
    {
        Validate(new EmailOptions { Host = host, Username = "mailer-account" }).ShouldHaveSingleItem();
    }

    private static List<ValidationResult> Validate(EmailOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
