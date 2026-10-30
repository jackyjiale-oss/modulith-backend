using Microsoft.Extensions.Options;
using TemplateName.Modules.Auth.Application.Abstractions;

namespace TemplateName.Modules.Auth.Infrastructure.External;

/// <summary>
/// The providers of <c>Auth:External:Providers</c>, resolved once from the options (a change needs a restart): each enabled entry is merged
/// with its preset into an <see cref="ExternalProvider"/> under its trimmed lower-case name. The options are validated at start-up, so an
/// enabled entry is trusted to be well-formed here; a disabled one only lends its display name.
/// </summary>
internal sealed class ExternalProviderCatalog : IExternalProviderCatalog
{
    private readonly Dictionary<string, ExternalProvider> _enabledByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _displayNames = new(StringComparer.Ordinal);

    public ExternalProviderCatalog(IOptions<ExternalLoginOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var (key, provider) in options.Value.Providers)
        {
            if (provider is null)
            {
                continue;
            }

            var name = ExternalProviderPresets.NormalizeName(key);
            var displayName = DisplayNameFor(key, name, provider);
            _displayNames[name] = displayName;

            if (provider.Enabled)
            {
                _enabledByName[name] = Build(name, displayName, provider);
            }
        }

        Enabled = [.. _enabledByName.Values.OrderBy(provider => provider.Name, StringComparer.Ordinal)];
    }

    public IReadOnlyList<ExternalProvider> Enabled { get; }

    public ExternalProvider? Find(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return _enabledByName.GetValueOrDefault(ExternalProviderPresets.NormalizeName(name));
    }

    public string DisplayNameOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return _displayNames.GetValueOrDefault(ExternalProviderPresets.NormalizeName(name), name);
    }

    private static string DisplayNameFor(string key, string name, ExternalProviderOptions provider)
    {
        if (!string.IsNullOrWhiteSpace(provider.DisplayName))
        {
            return provider.DisplayName.Trim();
        }

        return ExternalProviderPresets.TryGet(name, out var preset) ? preset.DisplayName : key.Trim();
    }

    private static ExternalProvider Build(string name, string displayName, ExternalProviderOptions options)
    {
        var clientIds = Trimmed(options.ClientIds);

        if (!ExternalProviderPresets.TryGet(name, out var preset))
        {
            var authority = options.Authority!.Trim();
            return new ExternalProvider(
                name,
                displayName,
                ExternalProviderKind.Oidc,
                authority,
                [authority],
                clientIds,
                ExternalEmailRule.EmailVerifiedClaim,
                options.TrustEmail ?? false,
                TenantId: null,
                AppId: null,
                AppSecret: null);
        }

        // The rule decides whether the email can ever be used; a TrustEmail that cannot matter is reported off, so no caller needs the rule to read it.
        var trustEmail = preset.EmailRule != ExternalEmailRule.Never && (options.TrustEmail ?? preset.TrustEmail);

        if (preset.Kind == ExternalProviderKind.Facebook)
        {
            var appId = options.AppId!.Trim();
            return new ExternalProvider(
                name,
                displayName,
                preset.Kind,
                preset.Authority,
                preset.Issuers,
                [appId],
                preset.EmailRule,
                trustEmail,
                TenantId: null,
                AppId: appId,
                AppSecret: options.AppSecret!.Trim());
        }

        if (name == ExternalProviderPresets.Microsoft)
        {
            ExternalProviderPresets.TryNormalizeTenant(options.TenantId, out var tenantId);
            return new ExternalProvider(
                name,
                displayName,
                preset.Kind,
                ExternalProviderPresets.MicrosoftAuthority(tenantId),
                preset.Issuers,
                clientIds,
                preset.EmailRule,
                trustEmail,
                tenantId,
                AppId: null,
                AppSecret: null);
        }

        return new ExternalProvider(
            name,
            displayName,
            preset.Kind,
            preset.Authority,
            preset.Issuers,
            clientIds,
            preset.EmailRule,
            trustEmail,
            TenantId: null,
            AppId: null,
            AppSecret: null);
    }

    private static string[] Trimmed(List<string> values) => [.. values.Select(value => value.Trim())];
}
