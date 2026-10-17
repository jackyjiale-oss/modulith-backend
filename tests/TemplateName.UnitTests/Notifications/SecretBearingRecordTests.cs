using System.Reflection;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Application.Inbox.List;
using TemplateName.Modules.Notifications.Application.Scheduling;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Templates;

namespace TemplateName.UnitTests.Notifications;

/// <summary>
/// The records that carry an address, a rendered text, a variable value or a decrypted link override <c>PrintMembers</c>, so the
/// compiler-generated <c>ToString</c> (a log line, a debugger, an assertion message) prints their ids and codes but never those values.
/// </summary>
public sealed class SecretBearingRecordTests
{
    private const string Address = "alice@example.com";
    private const string Link = "https://app.example.test/reset?token=secret-token-5b1e";
    private const string Subject = "Reset the password of Alice";
    private const string TextBody = "Hello Alice, open " + Link;
    private const string HtmlBody = "<p>Hello Alice, <a href=\"" + Link + "\">reset</a></p>";
    private const string DisplayName = "Alice Example";

    private static readonly Guid DeliveryId = Guid.CreateVersion7();
    private static readonly Guid NotificationId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();

    private static readonly string[] Values = [Address, Link, Subject, TextBody, HtmlBody, DisplayName, "secret-token", "Alice"];

    [Fact]
    public void Email_message_prints_none_of_its_values()
    {
        var text = new EmailMessage(Address, Subject, TextBody, HtmlBody).ToString();

        ShouldPrintNoValue(text, nameof(EmailMessage));
    }

    [Fact]
    public void Rendered_message_prints_none_of_its_values()
    {
        var text = new RenderedMessage(Subject, TextBody, HtmlBody).ToString();

        ShouldPrintNoValue(text, nameof(RenderedMessage));
    }

    [Fact]
    public void Delivery_work_prints_its_ids_but_not_the_destination_or_message()
    {
        var text = new DeliveryWork(
            DeliveryId, NotificationId, UserId, "auth.password_reset", NotificationCategory.Security, Address, new RenderedMessage(Subject, TextBody, HtmlBody)).ToString();

        ShouldPrintNoValue(text, nameof(DeliveryWork));
        text.ShouldContain(DeliveryId.ToString());
        text.ShouldContain("auth.password_reset");
    }

    [Fact]
    public void Notification_request_prints_its_ids_but_not_the_variables_or_address()
    {
        var text = new NotificationRequest(
            "auth.password_reset",
            UserId,
            NotificationId,
            "TemplateName.Tests.Consumer",
            new Dictionary<string, string> { ["display_name"] = DisplayName },
            new Dictionary<string, string> { ["action_url"] = Link },
            Address,
            ExpiresAt: null).ToString();

        ShouldPrintNoValue(text, nameof(NotificationRequest));
        text.ShouldContain(UserId.ToString());
        text.ShouldContain("auth.password_reset");
    }

    [Fact]
    public void Pushed_and_listed_in_app_notifications_print_their_ids_but_not_the_texts()
    {
        var pushed = new InAppNotificationPushed(DeliveryId, "auth.password_changed", NotificationCategory.Security, Subject, TextBody, DateTime.UnixEpoch).ToString();
        var listed = new InAppNotificationResponse(DeliveryId, "auth.password_changed", NotificationCategory.Security, Subject, TextBody, DateTime.UnixEpoch, ReadAt: null).ToString();

        ShouldPrintNoValue(pushed, nameof(InAppNotificationPushed));
        ShouldPrintNoValue(listed, nameof(InAppNotificationResponse));
        pushed.ShouldContain(DeliveryId.ToString());
        listed.ShouldContain(DeliveryId.ToString());
    }

    [Fact]
    public void Render_request_prints_its_codes_but_not_the_variables()
    {
        var type = typeof(ScribanNotificationRenderer).GetNestedType("RenderRequest", BindingFlags.NonPublic).ShouldNotBeNull();
        var request = Activator.CreateInstance(
            type,
            "auth.password_reset",
            NotificationChannel.Email,
            "en",
            new Dictionary<string, string> { ["action_url"] = Link, ["display_name"] = DisplayName },
            "TemplateName").ShouldNotBeNull();

        var text = request.ToString().ShouldNotBeNull();

        ShouldPrintNoValue(text, "RenderRequest");
        text.ShouldContain("auth.password_reset");
    }

    private static void ShouldPrintNoValue(string text, string typeName)
    {
        text.ShouldStartWith(typeName + " {");
        foreach (var value in Values)
        {
            text.ShouldNotContain(value, Case.Insensitive);
        }
    }
}
