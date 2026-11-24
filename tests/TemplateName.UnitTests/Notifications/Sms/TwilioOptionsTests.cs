using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Notifications;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Sms;
using TemplateName.Modules.Notifications.Infrastructure.Channels.Sms;

namespace TemplateName.UnitTests.Notifications.Sms;

public sealed class TwilioOptionsTests
{
    private const string AccountSid = "ACtest-account-sid";
    private const string AuthToken = "twilio-auth-token-secret";

    [Fact]
    public void Disabled_needs_nothing_and_registers_no_channel()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        services.GetRequiredService<IOptions<SmsOptions>>().Value.Enabled.ShouldBeFalse();
        services.GetServices<INotificationChannel>().ShouldNotContain(channel => channel is SmsChannel);
    }

    [Fact]
    public void Enabled_without_credentials_is_refused_naming_the_keys_only()
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Notifications:Sms:Enabled"] = "true" });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("Notifications:Sms:Twilio:AccountSid");
        failure.Message.ShouldContain("Notifications:Sms:Twilio:AuthToken");
        failure.Message.ShouldContain("Notifications:Sms:Twilio:From");
    }

    [Fact]
    public void Both_from_and_service_are_refused()
    {
        using var services = BuildServices(Enabled(new Dictionary<string, string?>
        {
            ["Notifications:Sms:Twilio:From"] = "+15005550006",
            ["Notifications:Sms:Twilio:MessagingServiceSid"] = "MG0123456789abcdef0123456789abcdef",
        }));

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("MessagingServiceSid");
        failure.Message.ShouldNotContain(AuthToken);
    }

    [Fact]
    public void A_from_that_is_not_a_number_in_e164_is_refused()
    {
        using var services = BuildServices(Enabled(new Dictionary<string, string?> { ["Notifications:Sms:Twilio:From"] = "Acme" }));

        Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("Notifications:Sms:Twilio:From");
    }

    [Theory]
    [InlineData("Notifications:Sms:Twilio:From", "+15005550006")]
    [InlineData("Notifications:Sms:Twilio:MessagingServiceSid", "MG0123456789abcdef0123456789abcdef")]
    public void Enabled_with_credentials_and_one_sender_starts_and_registers_the_channel(string key, string value)
    {
        using var services = BuildServices(Enabled(new Dictionary<string, string?> { [key] = value }));

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
        services.GetServices<INotificationChannel>().ShouldContain(channel => channel is SmsChannel);
        services.GetRequiredService<ISmsTransport>().ShouldBeOfType<TwilioSmsTransport>();
    }

    [Fact]
    public void A_provider_other_than_twilio_is_refused()
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Notifications:Sms:Provider"] = "vonage" });

        Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("Provider");
    }

    private static Dictionary<string, string?> Enabled(Dictionary<string, string?> settings)
    {
        settings["Notifications:Sms:Enabled"] = "true";
        settings["Notifications:Sms:Twilio:AccountSid"] = AccountSid;
        settings["Notifications:Sms:Twilio:AuthToken"] = AuthToken;
        return settings;
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddHttpClient();
        NotificationsModule.AddSms(services, configuration);
        return services.BuildServiceProvider();
    }
}
