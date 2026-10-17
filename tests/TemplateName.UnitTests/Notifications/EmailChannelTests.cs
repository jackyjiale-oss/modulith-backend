using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Channels.Email;

namespace TemplateName.UnitTests.Notifications;

public sealed class EmailChannelTests
{
    private const string Address = "alice@example.com";

    private readonly IEmailTransport _transport = Substitute.For<IEmailTransport>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sends_the_rendered_message_to_the_delivery_destination()
    {
        var channel = new EmailChannel(_transport);
        var work = Work(Address);

        var result = await channel.SendAsync(work, Ct);

        result.ShouldBe(ChannelSendResult.Sent);
        channel.Channel.ShouldBe(NotificationChannel.Email);
        await _transport.Received(1).SendAsync(
            new EmailMessage(Address, "Reset your password", "Open http://localhost:3000/reset?token=abc", "<p>html</p>"),
            Ct);
    }

    [Theory]
    [InlineData(true, "smtp_550")]
    [InlineData(true, "invalid_address")]
    [InlineData(false, "smtp_451")]
    [InlineData(false, "timeout")]
    [InlineData(false, "connection_failed")]
    public async Task Maps_permanent_and_transient_failures(bool isPermanent, string reason)
    {
        _transport.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).ThrowsAsync(new EmailSendException(isPermanent, reason));

        var result = await new EmailChannel(_transport).SendAsync(Work(Address), Ct);

        result.ShouldBe(isPermanent ? ChannelSendResult.Permanent(reason) : ChannelSendResult.Transient(reason));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_reason_that_is_not_a_short_code_becomes_unknown(bool isPermanent)
    {
        // A transport must answer with a short code; should one ever quote the server, the address must not reach LastError or a log.
        _transport.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new EmailSendException(isPermanent, "550 5.1.1 <alice@example.com> rejected"));

        var result = await new EmailChannel(_transport).SendAsync(Work(Address), Ct);

        result.ShouldBe(isPermanent ? ChannelSendResult.Permanent("unknown") : ChannelSendResult.Transient("unknown"));
    }

    [Fact]
    public void Exception_message_is_the_reason_only()
    {
        var exception = new EmailSendException(isPermanent: true, "smtp_550");

        exception.Message.ShouldBe("smtp_550");
        exception.Reason.ShouldBe("smtp_550");
        exception.IsPermanent.ShouldBeTrue();
        exception.InnerException.ShouldBeNull();
    }

    [Fact]
    public async Task Delivery_without_a_destination_is_a_permanent_invalid_address()
    {
        var result = await new EmailChannel(_transport).SendAsync(Work(destination: null), Ct);

        result.ShouldBe(ChannelSendResult.Permanent("invalid_address"));
        await _transport.DidNotReceiveWithAnyArgs().SendAsync(default!, Ct);
    }

    [Fact]
    public async Task Other_exceptions_reach_the_delivery_worker()
    {
        // The worker counts them as a transient channel_error and logs only their type.
        _transport.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(() => new EmailChannel(_transport).SendAsync(Work(Address), Ct));
    }

    private static DeliveryWork Work(string? destination) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "auth.password_reset",
        NotificationCategory.Security,
        destination,
        new RenderedMessage("Reset your password", "Open http://localhost:3000/reset?token=abc", "<p>html</p>"));
}
