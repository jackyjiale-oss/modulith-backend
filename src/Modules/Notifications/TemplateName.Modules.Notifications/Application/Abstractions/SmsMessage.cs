using System.Text;

namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>One text for <see cref="ISmsTransport"/>. <c>ToString</c> prints the delivery id only: the number and the text (a code) never.</summary>
/// <param name="DeliveryId">The delivery being sent, for the logs.</param>
/// <param name="To">The number in E.164.</param>
/// <param name="Body">The rendered text.</param>
internal sealed record SmsMessage(Guid DeliveryId, string To, string Body)
{
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("DeliveryId = ").Append(DeliveryId);
        return true;
    }
}
