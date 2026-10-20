using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Retention;

namespace TemplateName.UnitTests.Notifications;

public sealed class NotificationsRetentionOptionsTests
{
    [Fact]
    public void Defaults_match_the_spec()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        var options = services.GetRequiredService<IOptions<NotificationsRetentionOptions>>().Value;
        NotificationsRetentionOptions.SectionName.ShouldBe("Notifications:Retention");
        options.HubTickets.ShouldBe(TimeSpan.FromHours(1));
        options.Notifications.ShouldBe(TimeSpan.FromDays(180));
        options.InAppNotifications.ShouldBe(TimeSpan.FromDays(180));
    }

    [Theory]
    [InlineData("HubTickets")]
    [InlineData("Notifications")]
    [InlineData("InAppNotifications")]
    public void Negative_retention_is_rejected(string key)
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"Notifications:Retention:{key}"] = "-1.00:00:00" });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.OptionsType.ShouldBe(typeof(NotificationsRetentionOptions));
        failure.Message.ShouldContain(key);
    }

    [Theory]
    [InlineData("HubTickets")]
    [InlineData("Notifications")]
    [InlineData("InAppNotifications")]
    public void Zero_and_null_turn_a_cleanup_off_and_are_accepted(string key)
    {
        using var zero = BuildServices(new Dictionary<string, string?> { [$"Notifications:Retention:{key}"] = "00:00:00" });
        using var blank = BuildServices(new Dictionary<string, string?> { [$"Notifications:Retention:{key}"] = string.Empty });

        Should.NotThrow(() => zero.GetRequiredService<IStartupValidator>().Validate());
        Should.NotThrow(() => blank.GetRequiredService<IStartupValidator>().Validate());
        var zeroed = zero.GetRequiredService<IOptions<NotificationsRetentionOptions>>().Value;
        zeroed.GetType().GetProperty(key)!.GetValue(zeroed).ShouldBe(TimeSpan.Zero);
        var blanked = blank.GetRequiredService<IOptions<NotificationsRetentionOptions>>().Value;
        blanked.GetType().GetProperty(key)!.GetValue(blanked).ShouldBeNull();
    }

    [Fact]
    public void Each_key_binds_to_its_own_property()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            ["Notifications:Retention:HubTickets"] = "02:00:00",
            ["Notifications:Retention:Notifications"] = "30.00:00:00",
            ["Notifications:Retention:InAppNotifications"] = "365.00:00:00",
        });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        var options = services.GetRequiredService<IOptions<NotificationsRetentionOptions>>().Value;
        options.HubTickets.ShouldBe(TimeSpan.FromHours(2));
        options.Notifications.ShouldBe(TimeSpan.FromDays(30));
        options.InAppNotifications.ShouldBe(TimeSpan.FromDays(365));
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        NotificationsModule.AddRetention(services, configuration);
        return services.BuildServiceProvider();
    }
}
