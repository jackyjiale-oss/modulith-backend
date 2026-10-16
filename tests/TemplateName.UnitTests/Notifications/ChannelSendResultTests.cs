using TemplateName.Modules.Notifications.Application.Abstractions;

namespace TemplateName.UnitTests.Notifications;

public sealed class ChannelSendResultTests
{
    [Fact]
    public void Results_carry_the_outcome_and_the_reason_code()
    {
        ChannelSendResult.Sent.ShouldBe(new ChannelSendResult(ChannelSendOutcome.Sent, null));
        ChannelSendResult.Transient("smtp_451").ShouldBe(new ChannelSendResult(ChannelSendOutcome.TransientFailure, "smtp_451"));
        ChannelSendResult.Permanent("invalid_address").ShouldBe(new ChannelSendResult(ChannelSendOutcome.PermanentFailure, "invalid_address"));
    }

    [Theory]
    [InlineData("smtp_550", true)]
    [InlineData("invalid_address", true)]
    [InlineData("timeout", true)]
    [InlineData("render_failed", true)]
    [InlineData("channel_unavailable", true)]
    [InlineData("attempts_exhausted", true)]
    [InlineData("a_code_of_exactly_fifty_characters_long_xxxxxxxxxx", true)]
    [InlineData("a_code_of_exactly_fifty_one_characters_long_xxxxxxx", false)]
    [InlineData("550_rejected", false)]
    [InlineData("<bob@example.org>", false)]
    [InlineData(null, false)]
    public void IsReasonCode_accepts_only_short_codes(string? reason, bool isReasonCode)
        => ChannelSendResult.IsReasonCode(reason).ShouldBe(isReasonCode);

    [Theory]
    [InlineData("")]
    [InlineData("SMTP_550")]
    [InlineData("550 5.1.1 <alice@example.com>: recipient rejected")]
    [InlineData("http://localhost:3000/reset-password?token=abc")]
    [InlineData("a_reason_code_that_is_too_long_to_be_a_short_code_x")]
    public void Reason_must_be_a_short_code(string reason)
    {
        // A reason ends up in LastError, logs and nothing else; free text could carry an address or a link.
        Should.Throw<ArgumentException>(() => ChannelSendResult.Transient(reason));
        Should.Throw<ArgumentException>(() => ChannelSendResult.Permanent(reason));
    }
}
