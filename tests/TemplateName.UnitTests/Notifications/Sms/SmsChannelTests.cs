using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TemplateName.Modules.Notifications.Application.Abstractions;
using TemplateName.Modules.Notifications.Domain.Notifications;
using TemplateName.Modules.Notifications.Infrastructure.Channels.Sms;

namespace TemplateName.UnitTests.Notifications.Sms;

public sealed class SmsChannelTests
{
    private const string Number = "+60123456789";
    private const string Text = "Acme: your verification code is 042137. It expires in 10 minutes.";
    private const string MessageSid = "SM0123456789abcdef0123456789abcdef";

    private readonly ISmsTransport _transport = Substitute.For<ISmsTransport>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Sends_the_rendered_text_to_the_destination()
    {
        var work = Work(Number);

        var result = await new SmsChannel(_transport).SendAsync(work, Ct);

        result.Outcome.ShouldBe(ChannelSendOutcome.Sent);
        await _transport.Received(1).SendAsync(
            Arg.Is<SmsMessage>(message => message.DeliveryId == work.DeliveryId && message.To == Number && message.Body == Text),
            Ct);
        new SmsChannel(_transport).Channel.ShouldBe(NotificationChannel.Sms);
    }

    [Fact]
    public async Task Returns_the_sid_with_the_sent_result()
    {
        _transport.SendAsync(Arg.Any<SmsMessage>(), Arg.Any<CancellationToken>()).Returns(MessageSid);

        var result = await new SmsChannel(_transport).SendAsync(Work(Number), Ct);

        result.Outcome.ShouldBe(ChannelSendOutcome.Sent);
        result.ProviderMessageId.ShouldBe(MessageSid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SM 1 with text")]
    public async Task Without_a_usable_sid_the_result_is_plain_sent_and_nothing_throws(string? sid)
    {
        _transport.SendAsync(Arg.Any<SmsMessage>(), Arg.Any<CancellationToken>()).Returns(sid);

        var result = await new SmsChannel(_transport).SendAsync(Work(Number), Ct);

        result.ShouldBe(ChannelSendResult.Sent);
    }

    [Theory]
    [InlineData(true, "twilio_21211")]
    [InlineData(false, "twilio_http_503")]
    public async Task Maps_a_send_exception_to_its_result(bool permanent, string reason)
    {
        _transport.SendAsync(Arg.Any<SmsMessage>(), Arg.Any<CancellationToken>()).ThrowsAsync(new SmsSendException(permanent, reason));

        var result = await new SmsChannel(_transport).SendAsync(Work(Number), Ct);

        result.Outcome.ShouldBe(permanent ? ChannelSendOutcome.PermanentFailure : ChannelSendOutcome.TransientFailure);
        result.Reason.ShouldBe(reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0123456789")]
    [InlineData("+60 12 345 6789")]
    public async Task A_destination_that_is_not_a_number_is_permanent_and_sends_nothing(string? destination)
    {
        var result = await new SmsChannel(_transport).SendAsync(Work(destination), Ct);

        result.ShouldBe(ChannelSendResult.Permanent("invalid_number"));
        await _transport.DidNotReceiveWithAnyArgs().SendAsync(default!, Ct);
    }

    private static DeliveryWork Work(string? destination) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "auth.phone_verification",
            NotificationCategory.Security,
            destination,
            new RenderedMessage(string.Empty, Text, null),
            NotificationPriority.Critical);
}
