using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Notifications.Application.Sms;

/// <summary>
/// The SMS channel's settings that the application layer reads (section <c>Notifications:Sms</c>, validated on start). The Twilio
/// credentials are <c>Notifications:Sms:Twilio</c>, read by the transport only.
/// </summary>
internal sealed class SmsOptions : IValidatableObject
{
    internal const string SectionName = "Notifications:Sms";

    internal const string TwilioProvider = "twilio";

    /// <summary>
    /// Turns the SMS channel on. Off: no SMS delivery is created, the preferences API does not offer the channel, and an event of a type
    /// whose only channel is SMS (the phone code) is recorded with nothing to deliver.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>The SMS provider; <c>twilio</c> is the only one.</summary>
    public string Provider { get; set; } = TwilioProvider;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.Equals(Provider, TwilioProvider, StringComparison.OrdinalIgnoreCase))
        {
            yield return new ValidationResult($"{nameof(Provider)} must be '{TwilioProvider}', the only SMS provider.", [nameof(Provider)]);
        }
    }
}
