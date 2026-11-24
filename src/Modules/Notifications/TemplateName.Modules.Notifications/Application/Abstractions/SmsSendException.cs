namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// A text the provider did not accept. <see cref="Reason"/> is a short code (the whole message, and the <c>LastError</c> the delivery
/// keeps); the provider's answer is never attached, because it can quote the number.
/// </summary>
internal sealed class SmsSendException(bool isPermanent, string reason) : Exception(reason)
{
    public bool IsPermanent { get; } = isPermanent;

    public string Reason { get; } = reason;
}
