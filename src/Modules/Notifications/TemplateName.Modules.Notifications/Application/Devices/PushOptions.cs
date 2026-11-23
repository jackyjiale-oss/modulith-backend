using System.ComponentModel.DataAnnotations;

namespace TemplateName.Modules.Notifications.Application.Devices;

/// <summary>
/// The push channel's settings that the application layer reads (section <c>Notifications:Push</c>, validated on start). The FCM
/// credentials are <c>Notifications:Push:Fcm</c>, read by the transport only.
/// </summary>
internal sealed class PushOptions : IValidatableObject
{
    internal const string SectionName = "Notifications:Push";

    internal const string FcmProvider = "fcm";

    /// <summary>Turns the push channel on. Off: no push delivery is created and the preferences API does not offer the channel.</summary>
    public bool Enabled { get; set; }

    /// <summary>The push provider; <c>fcm</c> is the only one.</summary>
    public string Provider { get; set; } = FcmProvider;

    /// <summary>The most active devices a user has; registering one more deactivates the oldest.</summary>
    [Range(1, 100)]
    public int MaxDevicesPerUser { get; set; } = 10;

    /// <summary>The most device registrations one user makes per minute; beyond it, 429 <c>rate_limit.exceeded</c>.</summary>
    [Range(1, 1000)]
    public int RegistrationsPerMinute { get; set; } = 10;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // The message names the key, never the configured value.
        if (!string.Equals(Provider, FcmProvider, StringComparison.OrdinalIgnoreCase))
        {
            yield return new ValidationResult($"Notifications:Push:Provider must be '{FcmProvider}'.", [nameof(Provider)]);
        }
    }
}
