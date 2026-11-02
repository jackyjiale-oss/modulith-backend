using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Infrastructure.External;

/// <summary>
/// Checks every <b>enabled</b> provider of <see cref="ExternalLoginOptions"/> at start-up: a usable name, one entry per name, the right
/// <c>Type</c>, client IDs (Facebook: <c>AppId</c> and <c>AppSecret</c>), a generic entry's absolute <c>https</c> authority and a Microsoft
/// tenant. A disabled entry is not looked at. A message names the key and never quotes a configured value.
/// </summary>
internal sealed class ExternalLoginOptionsValidator : IValidateOptions<ExternalLoginOptions>
{
    /// <summary>The longest provider name; the <c>Provider</c> column is <c>nvarchar(64)</c>.</summary>
    internal const int MaxNameLength = 64;

    public ValidateOptionsResult Validate(string? name, ExternalLoginOptions options)
    {
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (key, provider) in options.Providers)
        {
            if (provider is null || !provider.Enabled)
            {
                continue;
            }

            var path = $"{ExternalLoginOptions.ProvidersPath}:{key}";
            var normalized = ExternalProviderPresets.NormalizeName(key);

            if (!IsValidName(normalized))
            {
                failures.Add($"{path} is not a usable provider name: use 1 to {MaxNameLength} lower-case letters, digits, '-' or '_', starting with a letter or digit (it is a route segment and is stored as it is).");
                continue;
            }

            if (!seen.Add(normalized))
            {
                failures.Add($"{path} names the same provider name as another enabled entry once case and surrounding spaces are ignored; keep one.");
                continue;
            }

            ExternalProviderPresets.TryGet(normalized, out var preset);
            ValidateType(provider, path, preset is null ? null : ExternalProviderPresets.TypeNameOf(preset.Kind), failures);

            if (preset is { Kind: ExternalProviderKind.Facebook })
            {
                ValidateFacebook(provider, path, failures);
                continue;
            }

            ValidateClientIds(provider, path, failures);

            if (preset is null)
            {
                ValidateAuthority(provider, path, failures);
            }
            else if (normalized == ExternalProviderPresets.Microsoft && !ExternalProviderPresets.TryNormalizeTenant(provider.TenantId, out _))
            {
                failures.Add($"{path}:{nameof(ExternalProviderOptions.TenantId)} must be common, organizations, consumers or a tenant GUID.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsValidName(string normalized)
    {
        if (normalized.Length is 0 or > MaxNameLength || !char.IsAsciiLetterOrDigit(normalized[0]))
        {
            return false;
        }

        foreach (var character in normalized)
        {
            if (!(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateType(ExternalProviderOptions provider, string path, string? presetType, List<string> failures)
    {
        var type = provider.Type?.Trim();
        if (string.IsNullOrEmpty(type))
        {
            if (presetType is null)
            {
                failures.Add($"{path}:{nameof(ExternalProviderOptions.Type)} must be \"{ExternalProviderPresets.OidcType}\" for a provider other than google, microsoft, apple and facebook.");
            }

            return;
        }

        var expected = presetType ?? ExternalProviderPresets.OidcType;
        if (!string.Equals(type, expected, StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{path}:{nameof(ExternalProviderOptions.Type)} must be \"{expected}\" or left out.");
        }
    }

    private static void ValidateFacebook(ExternalProviderOptions provider, string path, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(provider.AppId))
        {
            failures.Add($"{path}:{nameof(ExternalProviderOptions.AppId)} is required.");
        }

        if (string.IsNullOrWhiteSpace(provider.AppSecret))
        {
            failures.Add($"{path}:{nameof(ExternalProviderOptions.AppSecret)} is required (set it in user secrets or an environment variable, never in a file).");
        }
    }

    private static void ValidateClientIds(ExternalProviderOptions provider, string path, List<string> failures)
    {
        var clientIdsPath = $"{path}:{nameof(ExternalProviderOptions.ClientIds)}";
        if (provider.ClientIds is not { Count: > 0 })
        {
            failures.Add($"{clientIdsPath} needs at least one value.");
            return;
        }

        for (var index = 0; index < provider.ClientIds.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(provider.ClientIds[index]))
            {
                failures.Add($"{clientIdsPath}:{index} is blank.");
            }
        }
    }

    private static void ValidateAuthority(ExternalProviderOptions provider, string path, List<string> failures)
    {
        var authority = provider.Authority?.Trim();
        var valid = Uri.TryCreate(authority, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0;

        if (!valid)
        {
            failures.Add($"{path}:{nameof(ExternalProviderOptions.Authority)} must be an absolute https URL without credentials, a query or a fragment.");
        }
    }
}
