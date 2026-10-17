using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Notifications;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Templates;

namespace TemplateName.UnitTests.Notifications;

/// <summary>
/// Renders every Auth type, in every default channel and culture, through the real catalog, store and renderer with plausible values.
/// A typo in a placeholder (or a variable a template needs and the type does not declare) throws here instead of in the delivery worker.
/// </summary>
public sealed class AuthNotificationRenderTests
{
    private const string ProductName = "Acme Portal";
    private const string ActionUrl = "https://example.test/x?token=abc&lang=en";
    private const string EncodedActionUrl = "https://example.test/x?token=abc&amp;lang=en";
    private const string OccurredAt = "2026-10-09 14:30";
    private const string LockedUntil = "2026-10-09 15:45";
    private const string TimeZone = "Asia/Kuala_Lumpur";
    private const string ExpiresInMinutes = "60";

    private static readonly NotificationCatalog Catalog = BuildCatalog();
    private static readonly ScribanNotificationRenderer Renderer = new(
        new EmbeddedTemplateStore(Catalog),
        Options.Create(new TemplateOptions { ProductName = ProductName }));

    public static TheoryData<string, string> TypeAndCulture
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var type in BuildCatalog().All.OrderBy(type => type.Code, StringComparer.Ordinal))
            {
                foreach (var culture in RecipientCulture.Supported)
                {
                    data.Add(type.Code, culture);
                }
            }

            return data;
        }
    }

    [Fact]
    public void Catalog_holds_all_eight_auth_types()
    {
        Catalog.All.Count.ShouldBe(8);
    }

    [Theory]
    [MemberData(nameof(TypeAndCulture))]
    public void Every_default_channel_renders_in_every_culture(string typeCode, string culture)
    {
        var type = Catalog.Find(typeCode).ShouldNotBeNull();
        var variables = VariablesOf(type, "Aisha");

        foreach (var channel in type.DefaultChannels)
        {
            var message = Should.NotThrow(() => Renderer.Render(typeCode, channel, culture, variables), $"{typeCode} {channel} {culture}");

            message.Subject.ShouldNotBeNullOrWhiteSpace();
            message.Subject.ShouldNotContain('\n');
            message.Subject.ShouldNotContain('\r');
            message.Subject.ShouldNotContain("{{");
            message.Subject.ShouldNotContain("http", Case.Insensitive);
            message.TextBody.ShouldNotBeNullOrWhiteSpace();
            message.TextBody.ShouldNotContain("{{");

            if (channel == NotificationChannel.InApp)
            {
                message.HtmlBody.ShouldBeNull();
                message.TextBody.ShouldNotContain("http", Case.Insensitive);
                message.TextBody.Length.ShouldBeLessThan(300);
                continue;
            }

            message.HtmlBody.ShouldNotBeNull();
            message.HtmlBody.ShouldContain($"<html lang=\"{culture}\"");
            message.HtmlBody.ShouldContain(ProductName);
            message.HtmlBody.ShouldContain("Aisha");
            message.HtmlBody.ShouldNotContain("{{");
            message.TextBody.ShouldContain("Aisha");
            message.TextBody.ShouldNotContain("<");

            foreach (var name in type.Variables.Where(name => name != "display_name"))
            {
                message.TextBody.ShouldContain(variables[name], customMessage: $"{typeCode} {culture} text shows {name}");
                message.HtmlBody.ShouldContain(variables[name], customMessage: $"{typeCode} {culture} html shows {name}");
            }

            if (type.SecretVariables.Contains("action_url"))
            {
                message.HtmlBody.ShouldContain($"<a href=\"{EncodedActionUrl}\"");
                message.HtmlBody.ShouldNotContain("&amp;amp;");
                message.HtmlBody.ShouldNotContain("token=abc&lang");
                message.TextBody.Split('\n').ShouldContain(ActionUrl);
                message.TextBody.ShouldNotContain("&amp;");
            }
            else
            {
                message.HtmlBody.ShouldNotContain("<a ");
                message.TextBody.ShouldNotContain("http", Case.Insensitive);
            }
        }
    }

    [Theory]
    [MemberData(nameof(TypeAndCulture))]
    public void Display_name_is_encoded_in_html_and_literal_in_text(string typeCode, string culture)
    {
        const string DisplayName = "<b>x</b>";
        var type = Catalog.Find(typeCode).ShouldNotBeNull();

        var message = Renderer.Render(typeCode, NotificationChannel.Email, culture, VariablesOf(type, DisplayName));

        message.HtmlBody.ShouldNotBeNull();
        message.HtmlBody.ShouldContain("&lt;b&gt;x&lt;/b&gt;");
        message.HtmlBody.ShouldNotContain("<b>x</b>");
        message.TextBody.ShouldContain(DisplayName);
        message.Subject.ShouldNotContain("&lt;");
    }

    [Theory]
    [InlineData(AuthNotificationTypes.PasswordChanged)]
    [InlineData(AuthNotificationTypes.TokenReuseDetected)]
    public void In_app_messages_show_no_secret_and_no_link(string typeCode)
    {
        var type = Catalog.Find(typeCode).ShouldNotBeNull();
        var variables = VariablesOf(type, "Aisha");
        variables["action_url"] = ActionUrl;

        foreach (var culture in RecipientCulture.Supported)
        {
            var message = Renderer.Render(typeCode, NotificationChannel.InApp, culture, variables);

            message.Subject.ShouldNotContain("example.test");
            message.TextBody.ShouldNotContain("example.test");
            message.TextBody.ShouldNotContain("token=abc");
        }
    }

    private static Dictionary<string, string> VariablesOf(NotificationTypeDefinition type, string displayName)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["display_name"] = displayName,
            ["expires_in_minutes"] = ExpiresInMinutes,
            ["occurred_at"] = OccurredAt,
            ["locked_until"] = LockedUntil,
            ["time_zone"] = TimeZone,
            ["action_url"] = ActionUrl,
        };

        return type.Variables.Concat(type.SecretVariables).ToDictionary(name => name, name => values[name], StringComparer.Ordinal);
    }

    private static NotificationCatalog BuildCatalog()
    {
        var services = new ServiceCollection();
        services.AddNotificationsModule(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        return new NotificationCatalog(provider.GetServices<INotificationTypeSource>().ToList());
    }
}
