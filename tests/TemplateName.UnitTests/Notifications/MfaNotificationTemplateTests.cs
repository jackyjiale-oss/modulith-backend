using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Notifications;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Templates;

namespace TemplateName.UnitTests.Notifications;

/// <summary>
/// The wording of the two security notices Plan 4 adds, rendered through the real catalog, store and renderer: one distinct sentence
/// for every kind of MFA change in every culture, and the sign-in address shown only when the server observed one.
/// </summary>
public sealed class MfaNotificationTemplateTests
{
    private static readonly string[] ChangeKinds =
        ["enabled", "disabled", "recovery_codes_regenerated", "reset_by_admin", "recovery_code_used"];

    private static readonly NotificationCatalog Catalog = BuildCatalog();
    private static readonly ScribanNotificationRenderer Renderer = new(
        new EmbeddedTemplateStore(Catalog),
        Options.Create(new TemplateOptions { ProductName = "Acme Portal" }));

    public static TheoryData<string> Cultures => new(RecipientCulture.Supported);

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Mfa_changed_templates_render_a_distinct_sentence_for_every_kind(string culture)
    {
        var texts = new List<string>();
        var htmls = new List<string>();
        foreach (var kind in ChangeKinds)
        {
            var message = Renderer.Render(AuthNotificationTypes.MfaChanged, NotificationChannel.Email, culture, MfaVariables(kind));

            message.TextBody.ShouldNotBeNullOrWhiteSpace($"{culture} {kind} text");
            message.HtmlBody.ShouldNotBeNullOrWhiteSpace($"{culture} {kind} html");
            message.TextBody.ShouldContain("2026-10-09 14:30 (Asia/Kuala_Lumpur)", customMessage: $"{culture} {kind} text");
            message.HtmlBody.ShouldContain("2026-10-09 14:30 (Asia/Kuala_Lumpur)", customMessage: $"{culture} {kind} html");
            message.TextBody.ShouldNotContain(kind, Case.Insensitive);

            // A mistyped condition renders its sentence as nothing; the greeting and the time paragraphs would hide that, so the
            // sentence paragraph itself must not be empty (text: no blank-line run; html: no empty <p>), and the four paragraphs stay.
            var text = message.TextBody.Replace("\r\n", "\n", StringComparison.Ordinal);
            text.ShouldNotContain("\n\n\n", customMessage: $"{culture} {kind} text has an empty paragraph");
            Paragraphs(text).ShouldBe(4, $"{culture} {kind} text");
            Regex.IsMatch(message.HtmlBody, @"<p>\s*</p>").ShouldBeFalse($"{culture} {kind} html has an empty paragraph");
            texts.Add(message.TextBody);
            htmls.Add(message.HtmlBody);
        }

        texts.Distinct(StringComparer.Ordinal).Count().ShouldBe(ChangeKinds.Length, $"{culture} text");
        htmls.Distinct(StringComparer.Ordinal).Count().ShouldBe(ChangeKinds.Length, $"{culture} html");
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Mfa_changed_subject_is_one_line_in_every_culture(string culture)
    {
        var message = Renderer.Render(AuthNotificationTypes.MfaChanged, NotificationChannel.Email, culture, MfaVariables("enabled"));

        message.Subject.ShouldNotBeNullOrWhiteSpace();
        message.Subject.ShouldNotContain('\n');
    }

    [Theory]
    [InlineData("en", "Two-step verification was turned on for your account.", "Two-step verification was turned off")]
    [InlineData("ms", "Pengesahan dua langkah telah diaktifkan untuk akaun anda.", "Pengesahan dua langkah telah dimatikan")]
    [InlineData("zh-Hans", "您的账户已开启两步验证。", "您的账户已关闭两步验证")]
    public void Mfa_changed_shows_only_the_sentence_of_its_kind(string culture, string enabledSentence, string disabledSentence)
    {
        var message = Renderer.Render(AuthNotificationTypes.MfaChanged, NotificationChannel.Email, culture, MfaVariables("enabled"));

        message.TextBody.ShouldContain(enabledSentence);
        message.TextBody.ShouldNotContain(disabledSentence);
        message.HtmlBody.ShouldNotBeNull().ShouldContain(enabledSentence);
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void New_device_template_omits_the_address_line_when_unknown(string culture)
    {
        var known = Renderer.Render(AuthNotificationTypes.NewDeviceLogin, NotificationChannel.Email, culture, NewDeviceVariables("203.0.113.7"));
        var unknown = Renderer.Render(AuthNotificationTypes.NewDeviceLogin, NotificationChannel.Email, culture, NewDeviceVariables(""));

        known.TextBody.ShouldContain("203.0.113.7");
        known.HtmlBody.ShouldNotBeNull().ShouldContain("203.0.113.7");
        unknown.TextBody.ShouldNotContain("203.0.113.7");
        unknown.TextBody.ShouldNotContain("{{");
        unknown.HtmlBody.ShouldNotBeNull().ShouldNotContain("203.0.113.7");

        // One paragraph fewer, and no blank-line runs or empty <p> left behind where the address line was.
        Paragraphs(unknown.TextBody).ShouldBe(Paragraphs(known.TextBody) - 1, culture);
        unknown.TextBody.ShouldNotContain("\n\n\n");
        unknown.HtmlBody.ShouldNotContain("<p></p>");
        Regex.Count(unknown.HtmlBody, "<p[ >]").ShouldBe(Regex.Count(known.HtmlBody!, "<p[ >]") - 1, culture);
        unknown.TextBody.ShouldContain("2026-10-09 14:30 (Asia/Kuala_Lumpur)");
    }

    [Theory]
    [MemberData(nameof(Cultures))]
    public void New_device_address_is_encoded_in_html_and_literal_in_text(string culture)
    {
        var message = Renderer.Render(AuthNotificationTypes.NewDeviceLogin, NotificationChannel.Email, culture, NewDeviceVariables("<i>::1</i>"));

        message.TextBody.ShouldContain("<i>::1</i>");
        message.HtmlBody.ShouldNotBeNull().ShouldContain("&lt;i&gt;::1&lt;/i&gt;");
        message.HtmlBody.ShouldNotContain("<i>");
    }

    private static int Paragraphs(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Length;

    private static Dictionary<string, string> MfaVariables(string change) => new(StringComparer.Ordinal)
    {
        ["display_name"] = "Aisha",
        ["change"] = change,
        ["occurred_at"] = "2026-10-09 14:30",
        ["time_zone"] = "Asia/Kuala_Lumpur",
    };

    private static Dictionary<string, string> NewDeviceVariables(string ipAddress) => new(StringComparer.Ordinal)
    {
        ["display_name"] = "Aisha",
        ["occurred_at"] = "2026-10-09 14:30",
        ["time_zone"] = "Asia/Kuala_Lumpur",
        ["ip_address"] = ipAddress,
    };

    private static NotificationCatalog BuildCatalog()
    {
        var services = new ServiceCollection();
        services.AddNotificationsModule(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        return new NotificationCatalog(provider.GetServices<INotificationTypeSource>().ToList());
    }
}
