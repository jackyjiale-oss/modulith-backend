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
    private const string IpAddress = "203.0.113.7";
    private const string Provider = "Google";
    private const string ExpiresInMinutes = "60";
    private const string Code = "042137";

    // GSM 03.38's basic character set: a text of these alone is one segment up to 160 characters; anything else makes it UCS-2 (70).
    private const string Gsm7Basic =
        "@\u00a3$\u00a5\u00e8\u00e9\u00f9\u00ec\u00f2\u00c7\n\u00d8\u00f8\r\u00c5\u00e5\u0394_\u03a6\u0393\u039b\u03a9\u03a0\u03a8\u03a3\u0398\u039e\u00c6\u00e6\u00df\u00c9 !\"#\u00a4%&'()*+,-./0123456789:;<=>?\u00a1ABCDEFGHIJKLMNOPQRSTUVWXYZ\u00c4\u00d6\u00d1\u00dc\u00a7\u00bfabcdefghijklmnopqrstuvwxyz\u00e4\u00f6\u00f1\u00fc\u00e0";

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
    public void Catalog_holds_all_thirteen_auth_types()
    {
        Catalog.All.Count.ShouldBe(13);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ms")]
    [InlineData("zh-Hans")]
    public void The_sms_template_fits_one_segment(string culture)
    {
        var type = Catalog.Find(AuthNotificationTypes.PhoneVerification).ShouldNotBeNull();
        var variables = VariablesOf(type, "Aisha");
        variables["expires_in_minutes"] = "10";

        var message = Renderer.Render(AuthNotificationTypes.PhoneVerification, NotificationChannel.Sms, culture, variables);

        message.TextBody.ShouldContain(Code);
        message.TextBody.ShouldContain("10");
        message.TextBody.ShouldContain(ProductName);
        if (culture == "zh-Hans")
        {
            message.TextBody.Length.ShouldBeLessThanOrEqualTo(70, message.TextBody);
        }
        else
        {
            message.TextBody.All(Gsm7Basic.Contains).ShouldBeTrue(message.TextBody);
            message.TextBody.Length.ShouldBeLessThanOrEqualTo(160, message.TextBody);
        }
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

            if (channel == NotificationChannel.Sms)
            {
                // One line of text, no subject and no HTML; the code is the only secret it may show.
                message.Subject.ShouldBeEmpty();
                message.HtmlBody.ShouldBeNull();
                message.TextBody.ShouldNotBeNullOrWhiteSpace();
                message.TextBody.ShouldNotContain('\n');
                message.TextBody.ShouldNotContain("{{");
                message.TextBody.ShouldNotContain("http", Case.Insensitive);
                continue;
            }

            message.Subject.ShouldNotBeNullOrWhiteSpace();
            message.Subject.ShouldNotContain('\n');
            message.Subject.ShouldNotContain('\r');
            message.Subject.ShouldNotContain("{{");
            message.Subject.ShouldNotContain("http", Case.Insensitive);
            message.TextBody.ShouldNotBeNullOrWhiteSpace();
            message.TextBody.ShouldNotContain("{{");

            if (channel is NotificationChannel.InApp or NotificationChannel.Push)
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

            // "change" selects a sentence (MfaNotificationTemplateTests), so its value is not printed.
            foreach (var name in type.Variables.Where(name => name is not ("display_name" or "change")))
            {
                message.TextBody.ShouldContain(variables[name], customMessage: $"{typeCode} {culture} text shows {name}");
                message.HtmlBody.ShouldContain(variables[name], customMessage: $"{typeCode} {culture} html shows {name}");
            }

            if (type.SecretVariables.ContainsKey("action_url"))
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
        if (!type.DefaultChannels.Contains(NotificationChannel.Email))
        {
            return;
        }

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
    [InlineData(AuthNotificationTypes.ExternalLoginLinked)]
    [InlineData(AuthNotificationTypes.ExternalLoginUnlinked)]
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

    // A push shows on a lock screen: the server-observed address of a new sign-in stays in the email.
    [Theory]
    [InlineData(AuthNotificationTypes.NewDeviceLogin)]
    [InlineData(AuthNotificationTypes.TokenReuseDetected)]
    [InlineData(AuthNotificationTypes.AccountLocked)]
    public void Push_messages_show_no_address_and_stay_short(string typeCode)
    {
        var type = Catalog.Find(typeCode).ShouldNotBeNull();
        var variables = VariablesOf(type, "Aisha");

        foreach (var culture in RecipientCulture.Supported)
        {
            var message = Renderer.Render(typeCode, NotificationChannel.Push, culture, variables);

            message.Subject.Length.ShouldBeLessThanOrEqualTo(200, $"{typeCode} {culture}");
            message.TextBody.Length.ShouldBeLessThan(300, $"{typeCode} {culture}");
            message.TextBody.ShouldNotContain(IpAddress);
            message.HtmlBody.ShouldBeNull();
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
            ["change"] = "enabled",
            ["ip_address"] = IpAddress,
            ["provider"] = Provider,
            ["code"] = Code,
        };

        return type.Variables.Concat(type.SecretVariables.Keys).ToDictionary(name => name, name => values[name], StringComparer.Ordinal);
    }

    private static NotificationCatalog BuildCatalog()
    {
        var services = new ServiceCollection();
        services.AddNotificationsModule(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        return new NotificationCatalog(provider.GetServices<INotificationTypeSource>().ToList());
    }
}
