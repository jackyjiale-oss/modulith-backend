using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MimeKit;
using TemplateName.ArchitectureTests;
using TemplateName.Modules.Auth.Application.Abstractions;
using TemplateName.Modules.Auth.Infrastructure.Email;

namespace TemplateName.IntegrationTests.Auth;

public sealed class SmtpEmailSenderTests(MailpitFixture mailpit) : IClassFixture<MailpitFixture>
{
    [Fact]
    public async Task Sends_text_and_html_parts_with_from_and_subject()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await mailpit.ClearAsync(cancellationToken);
        var sender = SenderFor(mailpit.Host, mailpit.SmtpPort);
        var message = new EmailMessage("alice@example.com", "Confirm your email", "Open https://app.example.com/c?token=abc", "<html><body><p>Hello <b>Alice</b></p></body></html>");

        await sender.SendAsync(message, cancellationToken);

        var summaries = await mailpit.ListAsync(cancellationToken);
        summaries.Count.ShouldBe(1);
        var stored = await mailpit.GetAsync(summaries[0].Id, cancellationToken);
        stored.Subject.ShouldBe("Confirm your email");
        stored.From.Address.ShouldBe("no-reply@localhost.test");
        stored.From.Name.ShouldBe("TemplateName");
        stored.To.Select(address => address.Address).ShouldBe(["alice@example.com"]);
        stored.Text.ShouldContain("https://app.example.com/c?token=abc");
        stored.Html.ShouldContain("<b>Alice</b>");
    }

    [Fact]
    public async Task Sends_a_text_only_message_when_there_is_no_html_body()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await mailpit.ClearAsync(cancellationToken);

        await SenderFor(mailpit.Host, mailpit.SmtpPort).SendAsync(new EmailMessage("bob@example.com", "Plain", "Just text", null), cancellationToken);

        var summaries = await mailpit.ListAsync(cancellationToken);
        var stored = await mailpit.GetAsync(summaries.Single().Id, cancellationToken);
        stored.Text.ShouldContain("Just text");
        stored.Html.ShouldBeEmpty();
    }

    [Fact]
    public async Task Throws_when_the_server_is_unreachable()
    {
        var sender = SenderFor(IPAddress.Loopback.ToString(), UnusedPort());

        await Should.ThrowAsync<SocketException>(() => sender.SendAsync(
            new EmailMessage("alice@example.com", "Never sent", "text", null),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Throws_when_the_recipient_address_is_malformed()
    {
        var sender = SenderFor(mailpit.Host, mailpit.SmtpPort);

        await Should.ThrowAsync<ParseException>(() => sender.SendAsync(
            new EmailMessage("not an address", "Never sent", "text", null),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Honours_a_cancelled_token()
    {
        var sender = SenderFor(mailpit.Host, mailpit.SmtpPort);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => sender.SendAsync(
            new EmailMessage("alice@example.com", "Never sent", "text", null),
            cancellation.Token));
    }

    [Fact]
    public void Docker_compose_pins_the_same_Mailpit_image_as_the_tests()
    {
        var compose = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docker-compose.yml"));

        compose.ShouldContain($"image: {MailpitFixture.Image}");
    }

    private static SmtpEmailSender SenderFor(string host, int port)
        => new(Options.Create(new EmailOptions { Host = host, Port = port }), NullLogger<SmtpEmailSender>.Instance);

    private static int UnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
