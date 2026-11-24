namespace TemplateName.Modules.Notifications.Application.Abstractions;

/// <summary>
/// Sends one text to one number through the SMS provider (Twilio). It returns once the provider accepted the message, with the provider's
/// id for it (Twilio's message SID) or null when the answer held no usable id, and throws <see cref="SmsSendException"/> otherwise. It
/// never logs or throws the number or the text. Accepted is not delivered: the provider may still fail to reach the phone.
/// </summary>
internal interface ISmsTransport
{
    Task<string?> SendAsync(SmsMessage message, CancellationToken cancellationToken);
}
