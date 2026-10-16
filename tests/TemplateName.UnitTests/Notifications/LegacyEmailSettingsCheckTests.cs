using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Channels.Email;

namespace TemplateName.UnitTests.Notifications;

/// <summary>
/// D15: the SMTP settings moved from <c>Auth:Email</c> to <c>Notifications:Email</c>. A host that still has any old key, from a settings
/// file, user secrets, the environment or any other source, refuses to start and says where the settings went.
/// </summary>
public sealed class LegacyEmailSettingsCheckTests
{
    private const string Pointer = "Auth:Email has moved to Notifications:Email (see docs/modules/notifications.md#configuration).";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("settings file")]
    [InlineData("environment variable")]
    [InlineData("in-memory")]
    public async Task Old_section_stops_the_host_with_a_pointer(string source)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var variable = $"TNTEST{Guid.NewGuid():N}_";
        try
        {
            switch (source)
            {
                case "settings file":
                    // appsettings.*.json and user secrets are both JSON files.
                    builder.Configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""{ "Auth": { "Email": { "Host": "smtp.example.test" } } }""")));
                    break;
                case "environment variable":
                    Environment.SetEnvironmentVariable(variable + "Auth__Email__Password", "not-a-real-password");
                    builder.Configuration.AddEnvironmentVariables(variable);
                    break;
                default:
                    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Email:UseTls"] = "true" });
                    break;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable + "Auth__Email__Password", null);
        }

        NotificationsModule.AddEmailChannel(builder.Services, builder.Configuration);
        using var host = builder.Build();

        var exception = await Should.ThrowAsync<OptionsValidationException>(() => host.StartAsync(Ct));

        exception.OptionsType.ShouldBe(typeof(EmailOptions));
        exception.Failures.ShouldContain(Pointer);
        exception.Message.ShouldNotContain("smtp.example.test");
        exception.Message.ShouldNotContain("not-a-real-password");
    }

    [Fact]
    public async Task New_section_and_other_auth_sections_start_the_host()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:Email:Host"] = "smtp.example.test",
            ["Notifications:Email:Port"] = "587",
            ["Auth:Links:ConfirmEmailUrl"] = "https://app.example.test/confirm?token={token}",
            ["Auth:EmailLinks"] = "a sibling whose name only starts like the old section",
        });
        NotificationsModule.AddEmailChannel(builder.Services, builder.Configuration);
        using var host = builder.Build();

        await host.StartAsync(Ct);

        host.Services.GetRequiredService<IOptions<EmailOptions>>().Value.Port.ShouldBe(587);
        await host.StopAsync(Ct);
    }
}
