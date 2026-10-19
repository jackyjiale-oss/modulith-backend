using System.ComponentModel.DataAnnotations;
using System.Globalization;
using TemplateName.Modules.Notifications.Application.HubTickets;

namespace TemplateName.UnitTests.Notifications;

public sealed class HubOptionsTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        var options = new HubOptions();

        Validate(options).ShouldBeEmpty();
        HubOptions.SectionName.ShouldBe("Notifications:Hub");
        options.TicketLifetime.ShouldBe(TimeSpan.FromSeconds(30));
        options.ConnectionLifetime.ShouldBe(TimeSpan.FromMinutes(10));
        options.TicketsPerMinute.ShouldBe(30);
    }

    [Theory]
    [InlineData("00:00:05", "00:01:00", 1)]
    [InlineData("00:05:00", "01:00:00", 1000)]
    public void Range_limits_are_accepted(string ticketLifetime, string connectionLifetime, int ticketsPerMinute)
    {
        var options = new HubOptions
        {
            TicketLifetime = TimeSpan.Parse(ticketLifetime, CultureInfo.InvariantCulture),
            ConnectionLifetime = TimeSpan.Parse(connectionLifetime, CultureInfo.InvariantCulture),
            TicketsPerMinute = ticketsPerMinute,
        };

        Validate(options).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("00:00:04", "00:10:00", 30, nameof(HubOptions.TicketLifetime))]
    [InlineData("00:05:01", "00:10:00", 30, nameof(HubOptions.TicketLifetime))]
    [InlineData("00:00:30", "00:00:59", 30, nameof(HubOptions.ConnectionLifetime))]
    [InlineData("00:00:30", "01:00:01", 30, nameof(HubOptions.ConnectionLifetime))]
    [InlineData("00:00:30", "00:10:00", 0, nameof(HubOptions.TicketsPerMinute))]
    [InlineData("00:00:30", "00:10:00", 1001, nameof(HubOptions.TicketsPerMinute))]
    public void Out_of_range_values_are_rejected(string ticketLifetime, string connectionLifetime, int ticketsPerMinute, string member)
    {
        var options = new HubOptions
        {
            TicketLifetime = TimeSpan.Parse(ticketLifetime, CultureInfo.InvariantCulture),
            ConnectionLifetime = TimeSpan.Parse(connectionLifetime, CultureInfo.InvariantCulture),
            TicketsPerMinute = ticketsPerMinute,
        };

        Validate(options).ShouldHaveSingleItem().MemberNames.ShouldBe([member]);
    }

    private static List<ValidationResult> Validate(HubOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }
}
