namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>How a channel's attempt to deliver one message ended.</summary>
internal enum ChannelSendOutcome
{
    /// <summary>The channel accepted the message.</summary>
    Sent,

    /// <summary>The attempt failed but may succeed later (a timeout, a full mailbox, a server that is down); it is retried.</summary>
    TransientFailure,

    /// <summary>The attempt can never succeed (a rejected or malformed address, a message that cannot be rendered); no retry.</summary>
    PermanentFailure,
}
