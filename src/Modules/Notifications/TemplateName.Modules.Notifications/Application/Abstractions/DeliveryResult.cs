namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>How one attempt left a delivery: the <c>result</c> tag of the <c>notifications.deliveries</c> counter.</summary>
internal enum DeliveryResult
{
    /// <summary>The channel accepted the message (<c>sent</c>).</summary>
    Sent,

    /// <summary>A transient failure; the delivery waits for its next attempt (<c>retry</c>).</summary>
    Retry,

    /// <summary>A permanent failure, or the last attempt failed (<c>dead_lettered</c>).</summary>
    DeadLettered,

    /// <summary>The notification expired before it could be sent (<c>expired</c>).</summary>
    Expired,
}
