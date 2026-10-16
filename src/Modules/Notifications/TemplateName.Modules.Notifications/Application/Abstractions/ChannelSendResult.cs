using System.Text.RegularExpressions;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// What a channel answers for one delivery. <see cref="Reason"/> is a short reason code (<c>smtp_451</c>, <c>smtp_550</c>,
/// <c>invalid_address</c>, <c>timeout</c>, <c>render_failed</c>), never free text: it is stored in <c>Delivery.LastError</c> and logged,
/// so it must not be able to carry an address, a link or a server's message. <see cref="Transient"/> and <see cref="Permanent"/>
/// refuse anything else.
/// </summary>
internal sealed partial record ChannelSendResult(ChannelSendOutcome Outcome, string? Reason)
{
    /// <summary>The longest reason code.</summary>
    public const int MaxReasonLength = 64;

    /// <summary>The channel accepted the message.</summary>
    public static ChannelSendResult Sent { get; } = new(ChannelSendOutcome.Sent, null);

    /// <summary>A failure worth retrying, with its reason code.</summary>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is not a reason code.</exception>
    public static ChannelSendResult Transient(string reason) => new(ChannelSendOutcome.TransientFailure, EnsureReasonCode(reason));

    /// <summary>A failure that dead-letters the delivery at once, with its reason code.</summary>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is not a reason code.</exception>
    public static ChannelSendResult Permanent(string reason) => new(ChannelSendOutcome.PermanentFailure, EnsureReasonCode(reason));

    // The message names the rule only: the refused value may be the very text that must not be logged.
    private static string EnsureReasonCode(string reason) =>
        reason is not null && reason.Length <= MaxReasonLength && ReasonCodePattern().IsMatch(reason)
            ? reason
            : throw new ArgumentException(
                $"A reason is a code of lowercase letters, digits and underscores, starting with a letter, at most {MaxReasonLength} characters.",
                nameof(reason));

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonCodePattern();
}
