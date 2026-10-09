using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.IntegrationTests.Auth;

public sealed class RecordingEmailSenderTests
{
    [Fact]
    public async Task LastLinkToken_decodes_the_token_of_the_last_message_to_that_address()
    {
        var sender = new RecordingEmailSender();
        var cancellationToken = TestContext.Current.CancellationToken;
        await sender.SendAsync(new EmailMessage("Alice@Example.com", "one", "Open https://x.test/c?token=first", null), cancellationToken);
        await sender.SendAsync(new EmailMessage("bob@example.com", "two", "Open https://x.test/c?token=other", null), cancellationToken);
        await sender.SendAsync(new EmailMessage("alice@example.com", "three", "Open https://x.test/c?lang=en&token=a%2Bb%2Fc%3D\nThanks", null), cancellationToken);

        sender.LastLinkToken("ALICE@example.com").ShouldBe("a+b/c=");
        sender.Sent.Count.ShouldBe(3);
    }

    [Fact]
    public async Task LastLinkToken_fails_without_a_message_or_a_link_and_Clear_empties_the_list()
    {
        var sender = new RecordingEmailSender();
        await sender.SendAsync(new EmailMessage("alice@example.com", "no link", "Hello", null), TestContext.Current.CancellationToken);

        Should.Throw<InvalidOperationException>(() => sender.LastLinkToken("nobody@example.com"));
        Should.Throw<InvalidOperationException>(() => sender.LastLinkToken("alice@example.com"));

        sender.Clear();

        sender.Sent.ShouldBeEmpty();
    }
}
