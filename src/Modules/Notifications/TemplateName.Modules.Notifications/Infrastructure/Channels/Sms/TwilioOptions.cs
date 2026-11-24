using Microsoft.Extensions.Options;
using TemplateName.Modules.Notifications.Domain.Deliveries;

namespace TemplateName.Modules.Notifications.Infrastructure.Channels.Sms;

/// <summary>The Twilio transport's settings (section <c>Notifications:Sms:Twilio</c>), checked on start while SMS is enabled.</summary>
internal sealed class TwilioOptions
{
    internal const string SectionName = "Notifications:Sms:Twilio";

    /// <summary>The account SID (<c>AC...</c>); required while SMS is enabled.</summary>
    public string AccountSid { get; set; } = string.Empty;

    /// <summary>The account's auth token; required while SMS is enabled. A secret: user secrets or the environment, never a file.</summary>
    public string AuthToken { get; set; } = string.Empty;

    /// <summary>The sender number in E.164; exactly one of this and <see cref="MessagingServiceSid"/>.</summary>
    public string From { get; set; } = string.Empty;

    /// <summary>The Messaging Service (<c>MG...</c>) that picks the sender; exactly one of this and <see cref="From"/>.</summary>
    public string MessagingServiceSid { get; set; } = string.Empty;
}

/// <summary>
/// Refuses, while SMS is enabled, a missing account SID or auth token, no sender or both senders, and a <c>From</c> that is not a number
/// in E.164. The messages name the keys, never a value: the token is a secret. <paramref name="smsEnabled"/> is the raw
/// <c>Notifications:Sms:Enabled</c> read when the module registers, as the channel's registration is.
/// </summary>
internal sealed class TwilioOptionsValidation(bool smsEnabled) : IValidateOptions<TwilioOptions>
{
    public ValidateOptionsResult Validate(string? name, TwilioOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!smsEnabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.AccountSid))
        {
            failures.Add("Notifications:Sms:Twilio:AccountSid is required while Notifications:Sms:Enabled is true.");
        }

        if (string.IsNullOrWhiteSpace(options.AuthToken))
        {
            failures.Add("Notifications:Sms:Twilio:AuthToken is required while Notifications:Sms:Enabled is true (user secrets or the environment).");
        }

        var hasFrom = !string.IsNullOrWhiteSpace(options.From);
        var hasService = !string.IsNullOrWhiteSpace(options.MessagingServiceSid);
        if (hasFrom == hasService)
        {
            failures.Add("Exactly one of Notifications:Sms:Twilio:From and Notifications:Sms:Twilio:MessagingServiceSid is required while Notifications:Sms:Enabled is true.");
        }
        else if (hasFrom && !SmsDestination.IsValid(options.From))
        {
            failures.Add("Notifications:Sms:Twilio:From must be a number in E.164 (+ and 8 to 15 digits).");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
