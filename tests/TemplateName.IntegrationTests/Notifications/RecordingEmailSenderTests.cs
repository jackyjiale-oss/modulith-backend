using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.IntegrationTests.Notifications;

public sealed class RecordingEmailSenderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LastLinkToken_decodes_the_token_of_the_last_message_to_that_address()
    {
        var sender = new RecordingEmailSender();
        await sender.SendAsync(new EmailMessage("Alice@Example.com", "one", "Open https://x.test/c?token=first", null), Ct);
        await sender.SendAsync(new EmailMessage("bob@example.com", "two", "Open https://x.test/c?token=other", null), Ct);
        await sender.SendAsync(new EmailMessage("alice@example.com", "three", "Open https://x.test/c?lang=en&token=a%2Bb%2Fc%3D\nThanks", null), Ct);

        sender.LastLinkToken("ALICE@example.com").ShouldBe("a+b/c=");
        sender.Sent.Count.ShouldBe(3);
    }

    [Fact]
    public async Task LastLinkToken_reads_the_link_line_of_a_rendered_text_part_and_skips_later_notices()
    {
        // The templates put the link on a line of its own; a later notice without a link (password changed) does not hide it.
        var sender = new RecordingEmailSender();
        await sender.SendAsync(
            new EmailMessage("alice@example.com", "Reset your password", "Hello Alice,\n\nOpen this link:\n\nhttp://localhost:3000/reset-password?token=x%20y%26z\n\nThe link works once.", "<p>html</p>"),
            Ct);
        await sender.SendAsync(new EmailMessage("alice@example.com", "Your password was changed", "Hello Alice,\n\nYour password was changed.", "<p>html</p>"), Ct);

        sender.LastLinkToken("alice@example.com").ShouldBe("x y&z");
    }

    [Fact]
    public async Task LastLinkToken_fails_without_a_message_or_a_link_and_Clear_empties_the_list()
    {
        var sender = new RecordingEmailSender();
        await sender.SendAsync(new EmailMessage("alice@example.com", "no link", "Hello", null), Ct);

        Should.Throw<InvalidOperationException>(() => sender.LastLinkToken("nobody@example.com"));
        Should.Throw<InvalidOperationException>(() => sender.LastLinkToken("alice@example.com"));

        sender.Clear();

        sender.Sent.ShouldBeEmpty();
        sender.Attempts.ShouldBeEmpty();
    }

    [Fact]
    public async Task FailNext_fails_that_many_messages_then_accepts_again()
    {
        var sender = new RecordingEmailSender();
        var failure = new EmailSendException(isPermanent: true, "smtp_550");
        sender.FailNext(failure, times: 2);

        (await Should.ThrowAsync<EmailSendException>(() => sender.SendAsync(Message("a@example.com"), Ct))).ShouldBeSameAs(failure);
        (await Should.ThrowAsync<EmailSendException>(() => sender.SendAsync(Message("b@example.com"), Ct))).ShouldBeSameAs(failure);
        await sender.SendAsync(Message("c@example.com"), Ct);

        sender.Attempts.Select(message => message.To).ShouldBe(["a@example.com", "b@example.com", "c@example.com"]);
        sender.Sent.ShouldHaveSingleItem().To.ShouldBe("c@example.com");
        sender.AttemptsTo("A@example.com").ShouldBe(1);
    }

    [Fact]
    public async Task FailAlways_fails_every_message_until_Clear()
    {
        var sender = new RecordingEmailSender();
        sender.FailAlways(new EmailSendException(isPermanent: false, "timeout"));

        for (var i = 0; i < 3; i++)
        {
            (await Should.ThrowAsync<EmailSendException>(() => sender.SendAsync(Message("a@example.com"), Ct))).Reason.ShouldBe("timeout");
        }

        sender.Sent.ShouldBeEmpty();
        sender.AttemptsTo("a@example.com").ShouldBe(3);

        sender.Clear();
        await sender.SendAsync(Message("a@example.com"), Ct);
        sender.Sent.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Hold_parks_the_next_message_until_Release()
    {
        var sender = new RecordingEmailSender();
        var entered = sender.Hold();

        var sending = sender.SendAsync(Message("a@example.com"), Ct);
        (await entered.WaitAsync(TimeSpan.FromSeconds(10), Ct)).ShouldBe("a@example.com");
        sending.IsCompleted.ShouldBeFalse();
        sender.Sent.ShouldBeEmpty();

        sender.Release();
        await sending.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        sender.Sent.ShouldHaveSingleItem();
    }

    private static EmailMessage Message(string to) => new(to, "Subject", "Text", null);
}
