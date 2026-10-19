using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TemplateName.ArchitectureTests;
using TemplateName.IntegrationTests.Infrastructure;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Infrastructure.Channels.Email;

namespace TemplateName.IntegrationTests.Notifications;

/// <summary>
/// <c>SmtpEmailTransport</c> against a real SMTP server (Mailpit in a container): what it sends, and how it classifies failures into
/// short reason codes without ever putting the address in the reason, the exception or a log line.
/// </summary>
public sealed class SmtpEmailTransportTests(MailpitFixture mailpit) : IClassFixture<MailpitFixture>
{
    private readonly RecordingLoggerProvider _logs = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sends_text_and_html_parts_with_from_and_subject()
    {
        await mailpit.ClearAsync(Ct);
        var message = new EmailMessage("alice@example.com", "Confirm your email", "Open https://app.example.com/c?token=abc", "<html><body><p>Hello <b>Alice</b></p></body></html>");

        await TransportFor(mailpit.Host, mailpit.SmtpPort).SendAsync(message, Ct);

        var summaries = await mailpit.ListAsync(Ct);
        summaries.Count.ShouldBe(1);
        var stored = await mailpit.GetAsync(summaries[0].Id, Ct);
        stored.Subject.ShouldBe("Confirm your email");
        stored.From.Address.ShouldBe("no-reply@localhost.test");
        stored.From.Name.ShouldBe("TemplateName");
        stored.To.Select(address => address.Address).ShouldBe(["alice@example.com"]);
        stored.Text.ShouldContain("https://app.example.com/c?token=abc");
        stored.Html.ShouldContain("<b>Alice</b>");
        _logs.Entries.ShouldHaveSingleItem().ShouldContain("An email was sent");
        AssertNoLogMentions("alice@example.com", "token=abc");
    }

    [Fact]
    public async Task Sends_a_text_only_message_when_there_is_no_html_body()
    {
        await mailpit.ClearAsync(Ct);

        await TransportFor(mailpit.Host, mailpit.SmtpPort).SendAsync(new EmailMessage("bob@example.com", "Plain", "Just text", null), Ct);

        var summaries = await mailpit.ListAsync(Ct);
        var stored = await mailpit.GetAsync(summaries.Single().Id, Ct);
        stored.Text.ShouldContain("Just text");
        stored.Html.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rejected_recipient_is_classified_permanent()
    {
        // Mailpit answers RCPT TO with 550 for an address outside its allowed recipients, as a real server does for an unknown mailbox.
        await mailpit.ClearAsync(Ct);

        var exception = await Should.ThrowAsync<EmailSendException>(() => TransportFor(mailpit.Host, mailpit.SmtpPort).SendAsync(
            new EmailMessage(MailpitFixture.RejectedRecipient, "Never delivered", "text", null),
            Ct));

        exception.IsPermanent.ShouldBeTrue();
        exception.Reason.ShouldBe("smtp_550");
        exception.Message.ShouldBe("smtp_550");
        exception.InnerException.ShouldBeNull();
        (await mailpit.ListAsync(Ct)).ShouldBeEmpty();
        _logs.Entries.ShouldHaveSingleItem().ShouldContain("smtp_550");
        AssertNoLogMentions(MailpitFixture.RejectedRecipient, "rejected.example.net");
    }

    [Fact]
    public async Task Refused_sender_is_classified_transient()
    {
        // A 5xx reply to MAIL FROM (a wrong or unauthorised From) is a configuration fault: retried, so it can be fixed in the window.
        await mailpit.ClearAsync(Ct);
        await mailpit.RefuseSendersAsync(550, Ct);
        try
        {
            var exception = await Should.ThrowAsync<EmailSendException>(() => TransportFor(mailpit.Host, mailpit.SmtpPort).SendAsync(
                new EmailMessage("alice@example.com", "Never delivered", "text", null),
                Ct));

            exception.IsPermanent.ShouldBeFalse();
            exception.Reason.ShouldBe("smtp_550");
            exception.InnerException.ShouldBeNull();
            (await mailpit.ListAsync(Ct)).ShouldBeEmpty();
            _logs.Entries.ShouldHaveSingleItem().ShouldContain("smtp_550");
            AssertNoLogMentions("alice@example.com", "no-reply@localhost.test");
        }
        finally
        {
            await mailpit.RefuseSendersAsync(null, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Malformed_recipient_is_classified_permanent_invalid_address()
    {
        var exception = await Should.ThrowAsync<EmailSendException>(() => TransportFor(mailpit.Host, mailpit.SmtpPort).SendAsync(
            new EmailMessage("not an address", "Never sent", "text", null),
            Ct));

        exception.IsPermanent.ShouldBeTrue();
        exception.Reason.ShouldBe("invalid_address");
        exception.Message.ShouldBe("invalid_address");
        AssertNoLogMentions("not an address");
    }

    [Fact]
    public async Task Refused_connection_is_classified_transient_connection_failed()
    {
        var exception = await Should.ThrowAsync<EmailSendException>(() => TransportFor(IPAddress.Loopback.ToString(), UnusedPort()).SendAsync(
            new EmailMessage("alice@example.com", "Never sent", "text", null),
            Ct));

        exception.IsPermanent.ShouldBeFalse();
        exception.Reason.ShouldBe("connection_failed");
        exception.InnerException.ShouldBeNull();
        _logs.Entries.ShouldHaveSingleItem().ShouldContain(nameof(SocketException));
        AssertNoLogMentions("alice@example.com");
    }

    [Fact]
    public async Task Honours_a_cancelled_token()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => TransportFor(mailpit.Host, mailpit.SmtpPort).SendAsync(
            new EmailMessage("alice@example.com", "Never sent", "text", null),
            cancellation.Token));
    }

    [Fact]
    public void Docker_compose_pins_the_same_Mailpit_image_as_the_tests()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docker-compose.yml"));

        compose.ShouldContain($"image: {MailpitFixture.Image}");
    }

    private SmtpEmailTransport TransportFor(string host, int port)
        => new(Options.Create(new EmailOptions { Host = host, Port = port }), new LoggerFactory([_logs]).CreateLogger<SmtpEmailTransport>());

    private void AssertNoLogMentions(params string[] values)
        => _logs.Entries.ShouldAllBe(entry => values.All(value => !entry.Contains(value, StringComparison.OrdinalIgnoreCase)));

    private static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
