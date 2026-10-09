namespace TemplateName.Modules.Notifications.Domain.Deliveries;

/// <summary>Where a delivery is in its life. The values are persisted: never renumber or reuse them.</summary>
internal enum DeliveryStatus : byte
{
    /// <summary>Waiting for its next attempt (<c>NextAttemptAt</c>), the only status the delivery worker picks up.</summary>
    Pending = 0,

    /// <summary>The channel accepted the message.</summary>
    Sent = 1,

    /// <summary>Given up: a permanent failure or the last attempt failed. An administrator can retry it.</summary>
    DeadLettered = 2,

    /// <summary>The notification's expiry passed before the message was sent, so it never will be.</summary>
    Expired = 3,
}
