using System.Text;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// One push to one device. <paramref name="Token"/> is the device's FCM registration token, a bearer value; the title and body may be
/// personal. <c>ToString</c> prints the ids and <paramref name="Urgent"/> only.
/// </summary>
/// <param name="DeliveryId">The delivery being sent, for logs.</param>
/// <param name="DeviceId">The device, for logs.</param>
/// <param name="Token">The FCM registration token.</param>
/// <param name="Title">The rendered title.</param>
/// <param name="Body">The rendered body.</param>
/// <param name="Data">The data the app receives with the push: <c>notificationId</c>, <c>typeCode</c>, <c>category</c>.</param>
/// <param name="Urgent">High priority on Android and APNs priority 10: a <c>Critical</c> or <c>High</c> notification.</param>
internal sealed record PushMessage(
    Guid DeliveryId,
    Guid DeviceId,
    string Token,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Data,
    bool Urgent)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("DeliveryId = ").Append(DeliveryId)
            .Append(", DeviceId = ").Append(DeviceId)
            .Append(", Urgent = ").Append(Urgent);
        return true;
    }
}
