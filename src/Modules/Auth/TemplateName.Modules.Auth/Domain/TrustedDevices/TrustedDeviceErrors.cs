using TemplateName.SharedKernel;

namespace TemplateName.Modules.Auth.Domain.TrustedDevices;

/// <summary>The trusted device errors. Each code has a message in <c>Resources/AuthErrorMessages.resx</c> and its translations.</summary>
internal static class TrustedDeviceErrors
{
    public static Error NotFound(Guid id) =>
        Error.NotFound("auth.trusted_device_not_found", $"Trusted device '{id}' was not found.")
        with
        {
            Parameters = new Dictionary<string, object?> { ["id"] = id },
        };
}
