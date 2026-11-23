using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Notifications;

namespace TemplateName.UnitTests.Notifications.Push;

public sealed class FcmOptionsTests
{
    private const string ServiceAccountJson = """{"type":"service_account","project_id":"demo","private_key":"secret-key-value","client_email":"push@demo.iam.gserviceaccount.com"}""";

    [Fact]
    public void Disabled_needs_nothing()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void Enabled_without_project_id_is_refused()
    {
        using var services = BuildServices(new Dictionary<string, string?> { ["Notifications:Push:Enabled"] = "true" });

        Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate())
            .Message.ShouldContain("Notifications:Push:Fcm:ProjectId");
    }

    [Fact]
    public void Enabled_with_a_project_and_application_default_credentials_is_accepted()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            ["Notifications:Push:Enabled"] = "true",
            ["Notifications:Push:Fcm:ProjectId"] = "demo",
        });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Fact]
    public void A_service_account_json_is_accepted()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            ["Notifications:Push:Enabled"] = "true",
            ["Notifications:Push:Fcm:ProjectId"] = "demo",
            ["Notifications:Push:Fcm:CredentialsJson"] = ServiceAccountJson,
        });

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    [Theory]
    [InlineData("not json at all: secret-key-value")]
    [InlineData("""{"type":"authorized_user","private_key":"secret-key-value"}""")]
    [InlineData("""["secret-key-value"]""")]
    public void Unparsable_credentials_are_refused_without_their_value(string credentials)
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            ["Notifications:Push:Enabled"] = "true",
            ["Notifications:Push:Fcm:ProjectId"] = "demo",
            ["Notifications:Push:Fcm:CredentialsJson"] = credentials,
        });

        var failure = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        failure.Message.ShouldContain("Notifications:Push:Fcm:CredentialsJson");
        failure.Message.ShouldNotContain("secret-key-value");
    }

    private static ServiceProvider BuildServices(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        NotificationsModule.AddPush(services, configuration);
        return services.BuildServiceProvider();
    }
}
