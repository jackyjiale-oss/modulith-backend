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

    private static List<ValidationResult> Validate(EmailOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
