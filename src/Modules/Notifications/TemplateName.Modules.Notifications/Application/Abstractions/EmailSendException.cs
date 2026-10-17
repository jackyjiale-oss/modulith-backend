namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// An email could not be handed over. <see cref="Reason"/> is a short reason code (<c>smtp_550</c>, <c>invalid_address</c>,
/// <c>timeout</c>, <c>connection_failed</c>) and is also the whole message: never the server's reply, the address or the link, because
/// the reason is stored in <c>Delivery.LastError</c> and logged. For the same reason it never wraps the original exception, whose message
/// can quote the address. <see cref="IsPermanent"/> failures (a rejected or malformed address) are not retried.
/// </summary>
internal sealed class EmailSendException(bool isPermanent, string reason) : Exception(reason)
{
    /// <summary>Whether retrying cannot help: the delivery is dead-lettered at once.</summary>
    public bool IsPermanent { get; } = isPermanent;

    /// <summary>The short reason code.</summary>
    public string Reason { get; } = reason;
}
