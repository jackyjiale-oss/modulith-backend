namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// A push the provider did not accept. <see cref="Reason"/> is a short code (the whole message, and the <c>LastError</c> the delivery
/// keeps); the provider's answer is never attached, because it can quote the token. <see cref="DeactivateDevice"/> is set when the
/// provider said the token is no longer valid, so the device gets no further push.
/// </summary>
internal sealed class PushSendException(bool isPermanent, string reason, bool deactivateDevice) : Exception(reason)
{
    public bool IsPermanent { get; } = isPermanent;

    public string Reason { get; } = reason;

    public bool DeactivateDevice { get; } = deactivateDevice;
}
