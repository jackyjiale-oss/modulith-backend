using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Notifications;
using TemplateName.Modules.Notifications.Application.Devices;

namespace TemplateName.UnitTests.Notifications.Devices;

public sealed class PushOptionsTests
{
    [Fact]
    public void Defaults_are_off_fcm_10_and_10()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        var options = services.GetRequiredService<IOptions<PushOptions>>().Value;
        PushOptions.SectionName.ShouldBe("Notifications:Push");
        options.Enabled.ShouldBeFalse();
        options.Provider.ShouldBe("fcm");
        options.MaxDevicesPerUser.ShouldBe(10);
        options.RegistrationsPerMinute.ShouldBe(10);
    }

    [Fact]
    public void Unknown_provider_is_refused_without_its_value()
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Notifications:Push:Provider"] = "pigeon-post" });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("Notifications:Push:Provider");
        failure.Message.ShouldNotContain("pigeon-post");
    }

    [Fact]
    public void The_provider_is_matched_without_regard_to_case()
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Notifications:Push:Provider"] = "FCM" });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Theory]
    [InlineData("MaxDevicesPerUser", "0")]
    [InlineData("MaxDevicesPerUser", "101")]
    [InlineData("RegistrationsPerMinute", "0")]
    [InlineData("RegistrationsPerMinute", "1001")]
    public void Limits_out_of_range_are_refused(string key, string value)
    {
        using var services = BuildServices(new Dictionary<string, string?> { [$"Notifications:Push:{key}"] = value });

        Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate()).Message.ShouldContain(key);
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        NotificationsModule.AddPush(services, configuration);
        return services.BuildServiceProvider();
    }
}
