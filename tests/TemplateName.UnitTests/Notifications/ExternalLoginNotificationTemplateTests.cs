using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Notifications;
using TemplateName.Modules.Notifications.Application.Catalog;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Templates;

namespace TemplateName.UnitTests.Notifications;

/// <summary>
/// The wording of the two social-login security notices, rendered through the real catalog, store and renderer: what each says about
/// the provider, that the linked and the unlinked notice differ, and that the provider's name is data, not markup.
/// </summary>
public sealed class ExternalLoginNotificationTemplateTests
{
    private static readonly NotificationCatalog Catalog = BuildCatalog();
    private static readonly ScribanNotificationRenderer Renderer = new(
        new EmbeddedTemplateStore(Catalog),
        Options.Create(new TemplateOptions { ProductName = "Acme Portal" }));

    public static TheoryData<string> Cultures => new(RecipientCulture.Supported);

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Linked_and_unlinked_say_different_things_in_every_part(string culture)
    {
        foreach (var channel in new[] { NotificationChannel.Email, NotificationChannel.InApp })
        {
            var linked = Renderer.Render(AuthNotificationTypes.ExternalLoginLinked, channel, culture, Variables("Google"));
            var unlinked = Renderer.Render(AuthNotificationTypes.ExternalLoginUnlinked, channel, culture, Variables("Google"));

            linked.Subject.ShouldNotBe(unlinked.Subject, $"{culture} {channel} subject");
            linked.TextBody.ShouldNotBe(unlinked.TextBody, $"{culture} {channel} text");
            linked.TextBody.ShouldContain("Google", customMessage: $"{culture} {channel} linked");
            unlinked.TextBody.ShouldContain("Google", customMessage: $"{culture} {channel} unlinked");
            linked.TextBody.ShouldContain("2026-10-09 14:30 (Asia/Kuala_Lumpur)", customMessage: $"{culture} {channel} linked");
            unlinked.TextBody.ShouldContain("2026-10-09 14:30 (Asia/Kuala_Lumpur)", customMessage: $"{culture} {channel} unlinked");
        }
    }

    [Theory]
    [InlineData(AuthNotificationTypes.ExternalLoginLinked)]
    [InlineData(AuthNotificationTypes.ExternalLoginUnlinked)]
    public void Provider_name_is_encoded_in_html_and_literal_in_text(string typeCode)
    {
        foreach (var culture in RecipientCulture.Supported)
        {
            var message = Renderer.Render(typeCode, NotificationChannel.Email, culture, Variables("<i>Evil</i> & Co"));

            message.TextBody.ShouldContain("<i>Evil</i> & Co");
            message.HtmlBody.ShouldNotBeNull().ShouldContain("&lt;i&gt;Evil&lt;/i&gt; &amp; Co");
            message.HtmlBody.ShouldNotContain("<i>");
        }
    }

    [Theory]
    [InlineData(AuthNotificationTypes.ExternalLoginLinked, "added", "removed")]
    [InlineData(AuthNotificationTypes.ExternalLoginUnlinked, "removed", "added")]
    public void English_notice_says_whether_the_sign_in_method_was_added_or_removed(string typeCode, string expected, string other)
    {
        var message = Renderer.Render(typeCode, NotificationChannel.Email, "en", Variables("Google"));

        message.TextBody.ShouldContain(expected);
        message.TextBody.ShouldNotContain(other);
        message.HtmlBody.ShouldNotBeNull().ShouldContain(expected);
    }

    private static Dictionary<string, string> Variables(string provider) => new(StringComparer.Ordinal)
    {
        ["display_name"] = "Aisha",
        ["provider"] = provider,
        ["occurred_at"] = "2026-10-09 14:30",
        ["time_zone"] = "Asia/Kuala_Lumpur",
    };

    private static NotificationCatalog BuildCatalog()
    {
        var services = new ServiceCollection();
        services.AddNotificationsModule(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        return new NotificationCatalog(provider.GetServices<INotificationTypeSource>().ToList());
    }
}
