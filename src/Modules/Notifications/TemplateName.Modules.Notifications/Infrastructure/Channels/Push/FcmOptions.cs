using System.Text.Json;
using Microsoft.Extensions.Options;
using TemplateName.Modules.Notifications.Application.Devices;

namespace TemplateName.Modules.Notifications.Infrastructure.Channels.Push;

/// <summary>The FCM transport's settings (section <c>Notifications:Push:Fcm</c>), checked on start while push is enabled.</summary>
internal sealed class FcmOptions
{
    internal const string SectionName = "Notifications:Push:Fcm";

    /// <summary>The Firebase project id; required while push is enabled.</summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>
    /// The service-account JSON; empty means Google's application default credentials (<c>GOOGLE_APPLICATION_CREDENTIALS</c>, workload
    /// identity). A secret: user secrets or the environment, never a file.
    /// </summary>
    public string CredentialsJson { get; set; } = string.Empty;
}

/// <summary>
/// Refuses, while push is enabled, a missing project id and credentials that are not a service-account JSON object. The messages name
/// the keys, never a value: the credentials hold a private key. <paramref name="pushEnabled"/> is the raw
/// <c>Notifications:Push:Enabled</c> read when the module registers, as the channel's registration is, so this check does not depend
/// on <see cref="PushOptions"/> passing its own validation.
/// </summary>
internal sealed class FcmOptionsValidation(bool pushEnabled) : IValidateOptions<FcmOptions>
{
    public ValidateOptionsResult Validate(string? name, FcmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!pushEnabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.ProjectId))
        {
            failures.Add("Notifications:Push:Fcm:ProjectId is required while Notifications:Push:Enabled is true.");
        }

        if (!string.IsNullOrWhiteSpace(options.CredentialsJson) && !IsServiceAccountJson(options.CredentialsJson))
        {
            failures.Add("Notifications:Push:Fcm:CredentialsJson must be a service-account JSON object (\"type\": \"service_account\"), or empty for application default credentials.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsServiceAccountJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() == "service_account";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
